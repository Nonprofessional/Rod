using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;

namespace Rod.CoreState.Application;

/// <summary>
/// The first engagement use cases: create an engagement, and mint
/// a stager token for it. Orchestrates the core-state ports; holds no state of
/// its own. The owner is the authenticated operator the transport layer resolved
/// off the session principal  (operator auth); the
/// service trusts that caller to have already proven its identity.
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
    /// Creates an engagement owned by the authenticated operator. The owner is
    /// recorded as the engagement's single Owner member; its handle is resolved
    /// from the operator record so the response carries it without the caller
    /// having to supply it.
    /// </summary>
    public async Task<EngagementCreated> CreateEngagementAsync(
        CreateEngagementCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // The owner is the authenticated operator; auth guarantees the account
        // exists, so this is a resolve-for-handle, not a get-or-create.
        var owner = await _operators.GetOrThrowAsync(command.OwnerId, cancellationToken);

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

    /// <summary>
    /// Applies the engagement's rules-of-engagement profile (architecture.md
    /// Sec 9 -- ROE guardrails). The profile is the server-side scope of what
    /// the engagement's operators may task; task issuance refuses anything
    /// outside it before queuing. Applying an empty profile reopens the
    /// engagement (the unrestricted scope). The change is effective
    /// immediately for later issuances; the caller records it in the audit
    /// trail.
    /// </summary>
    public async Task<RoeApplied> ApplyRoeAsync(
        ApplyRoeCommand command,
        CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.GetOrThrowAsync(command.EngagementId, cancellationToken);
        engagement.ApplyRoe(command.Profile);
        await _engagements.SaveAsync(engagement, cancellationToken);

        return new RoeApplied(engagement.Id, engagement.Roe);
    }
}

/// <summary>
/// Request to create an engagement. The owner is the authenticated operator;
/// only the name is supplied by the caller.
/// </summary>
public sealed record CreateEngagementCommand(OperatorId OwnerId, string Name);

/// <summary>Result of creating an engagement.</summary>
public sealed record EngagementCreated(
    EngagementId EngagementId,
    string Name,
    OperatorId OwnerId,
    string OwnerHandle,
    DateTimeOffset CreatedAt);

/// <summary>Request to mint a stager token for an engagement's owner.</summary>
public sealed record MintStagerTokenCommand(EngagementId EngagementId);

/// <summary>Request to apply an engagement's rules-of-engagement profile.</summary>
public sealed record ApplyRoeCommand(EngagementId EngagementId, RoeProfile Profile);

/// <summary>Result of applying an ROE profile: the engagement and its scope now in force.</summary>
public sealed record RoeApplied(EngagementId EngagementId, RoeProfile Profile);

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
