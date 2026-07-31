using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;

namespace Rod.CoreState.Application;

/// <summary>
/// The first engagement use cases (roadmap M1.1): create an engagement, and mint
/// a stager token for it. Orchestrates the core-state ports; holds no state of
/// its own. The walking skeleton resolves the creating/issuing operator through
/// the request -- real operator authentication arrives with the operator layer
/// (M2.4).
/// </summary>
public sealed class EngagementService
{
    private readonly IOperatorRepository _operators;
    private readonly IEngagementRepository _engagements;
    private readonly IStagerTokenService _stagerTokens;
    private readonly TimeProvider _clock;

    public EngagementService(
        IOperatorRepository operators,
        IEngagementRepository engagements,
        IStagerTokenService stagerTokens,
        TimeProvider clock)
    {
        _operators = operators;
        _engagements = engagements;
        _stagerTokens = stagerTokens;
        _clock = clock;
    }

    /// <summary>
    /// Registers the operator (if new) and creates an engagement owned by them.
    /// The owner is recorded as the engagement's single Owner member.
    /// </summary>
    public async Task<EngagementCreated> CreateEngagementAsync(
        CreateEngagementCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // The skeleton resolves the operator from the request; M2.4 replaces this
        // with authenticated operator identity.
        var owner = await _operators.FindAsync(command.OwnerId, cancellationToken);
        if (owner is null)
        {
            owner = Operator.Register(command.OwnerId, command.OwnerHandle, command.OwnerDisplayName, now);
            await _operators.SaveAsync(owner, cancellationToken);
        }

        var engagement = Engagement.Create(EngagementId.New(), command.Name, owner.Id, now);
        await _engagements.SaveAsync(engagement, cancellationToken);

        return new EngagementCreated(
            engagement.Id,
            engagement.Name,
            owner.Id,
            owner.Handle,
            engagement.CreatedAt);
    }

    /// <summary>
    /// Mints a stager token for an engagement, issued by its owner. The secret is
    /// returned once; only the caller sees it.
    /// </summary>
    public async Task<StagerTokenMinted> MintStagerTokenForOwnerAsync(
        MintStagerTokenCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var engagement = await _engagements.GetOrThrowAsync(command.EngagementId, cancellationToken);
        var token = await _stagerTokens.MintAsync(engagement.Id, engagement.OwnerId, now, cancellationToken);

        return new StagerTokenMinted(
            token.Id,
            token.EngagementId,
            token.Secret,
            token.IssuedBy,
            token.IssuedAt,
            token.ExpiresAt,
            token.MaxUses);
    }
}

/// <summary>Request to create an engagement.</summary>
public sealed record CreateEngagementCommand(
    OperatorId OwnerId,
    string OwnerHandle,
    string OwnerDisplayName,
    string Name);

/// <summary>Result of creating an engagement.</summary>
public sealed record EngagementCreated(
    EngagementId EngagementId,
    string Name,
    OperatorId OwnerId,
    string OwnerHandle,
    DateTimeOffset CreatedAt);

/// <summary>Request to mint a stager token for an engagement's owner.</summary>
public sealed record MintStagerTokenCommand(EngagementId EngagementId);

/// <summary>
/// Result of minting a stager token. <see cref="Secret"/> is the plaintext,
/// shown exactly once at mint time.
/// </summary>
public sealed record StagerTokenMinted(
    StagerTokenId StagerTokenId,
    EngagementId EngagementId,
    string Secret,
    OperatorId IssuedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int MaxUses);
