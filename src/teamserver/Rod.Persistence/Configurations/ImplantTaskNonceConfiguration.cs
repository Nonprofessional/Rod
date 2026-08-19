using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the durable per-implant replay-nonce floor
/// (architecture.md Sec 9 -- tasking replay nonces). One row per implant that
/// negotiated the arm; the count is what the task repository's
/// <c>NextNonceAsync</c> increments and returns, so a restarted teamserver's
/// next dispatch for a negotiating implant continues past the pre-restart
/// count instead of resetting the floor.
/// </summary>
/// <remarks>
/// The row is task-store state, not implant state: the floor exists because
/// tasks were dispatched, it moves only with a dispatch, and it lives behind
/// the task repository the way the queue itself does. The increment is a raw
/// upsert (<c>INSERT ... ON CONFLICT DO UPDATE ... RETURNING</c>) in the
/// adapter -- atomic per row, so concurrent beacons for one implant never
/// reserve the same nonce; the entity exists so the migrations own the table.
/// </remarks>
internal sealed class StoredImplantTaskNonce
{
    public ImplantId ImplantId { get; set; }

    /// <summary>
    /// The reserved-nonce floor as a signed long: the wire counter is unsigned,
    /// but a floor that could not fit a bigint would outlive the engagement by
    /// orders of magnitude, and bigint keeps the column native.
    /// </summary>
    public long NonceFloor { get; set; }
}

internal sealed class StoredImplantTaskNonceConfiguration
    : IEntityTypeConfiguration<StoredImplantTaskNonce>
{
    public void Configure(EntityTypeBuilder<StoredImplantTaskNonce> builder)
    {
        builder.ToTable("implant_task_nonces");

        // implant_id is both the primary key and the foreign key to implants,
        // so each implant carries at most one floor. The typed id maps to a
        // Postgres uuid through IdConverters.ImplantId, matching the
        // implants.implant_id column the foreign key references.
        builder.HasKey(n => n.ImplantId);
        builder.Property(n => n.ImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("implant_id");

        // The reserved-nonce floor: 1 after the first reservation, monotonically
        // increasing after.
        builder.Property(n => n.NonceFloor).HasColumnName("nonce_floor").HasColumnType("bigint");

        // The floor cannot outlive its implant: cascade delete on the shared
        // implant_id keeps the row in sync if an implant is ever removed.
        builder.HasOne<global::Rod.CoreState.Implants.Implant>()
            .WithOne()
            .HasForeignKey<StoredImplantTaskNonce>(n => n.ImplantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
