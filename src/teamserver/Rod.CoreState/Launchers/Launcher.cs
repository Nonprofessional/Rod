using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;

namespace Rod.CoreState.Launchers;

/// <summary>
/// One rendered launcher the engagement keeps: a persisted cut of the
/// paste-ready stage-2 fetch one-liners, together with the download
/// credential that was minted for it. The row exists so an operator can come
/// back to a render -- re-copy the command, watch the credential's budget,
/// revoke it the moment it leaks, and tidy the list when it is spent --
/// without re-cutting anything (architecture.md Sec 6/8).
///
/// The row is a snapshot, not a live view: the URL, the front's name and
/// endpoint, and the policy are what the render chose. The credential itself
/// lives in the stager token store (hashed, counted, expiring); this row
/// carries the plaintext secret so the command can be re-copied -- the one
/// place a download credential is held in the clear, behind the operator
/// surface and deletable with the row. Deleting the payload behind a row
/// kills its fetch (the route 404s); deleting the row kills nothing -- the
/// credential dies by its own revocation or expiry.
/// </summary>
public sealed class Launcher
{
    public LauncherId Id { get; }
    public EngagementId EngagementId { get; }

    /// <summary>The stage-2 payload the fetch delivers.</summary>
    public Guid PayloadId { get; }

    /// <summary>The web listener whose front the fetch URL rides.</summary>
    public Guid ListenerId { get; }

    /// <summary>The front's name at render time -- a snapshot, so the row reads honestly after the listener is deleted or renamed.</summary>
    public string FrontName { get; }

    /// <summary>The front's public endpoint at render time, the same snapshot rule.</summary>
    public string FrontEndpoint { get; }

    /// <summary>The download credential this render minted; its budget and window live in the token store.</summary>
    public StagerTokenId TokenId { get; }

    /// <summary>
    /// The credential's plaintext, held so the command can be re-copied at
    /// any time. The token store keeps only a hash; this row is the clear
    /// copy, scoped behind the operator surface and gone when the row goes.
    /// </summary>
    public string TokenSecret { get; }

    /// <summary>The fetch URL the commands carry.</summary>
    public string Url { get; }

    /// <summary>The redeem budget the operator chose: N downloads, or 0 for unlimited-until-expiry.</summary>
    public int MaxUses { get; }

    /// <summary>When the credential stops working, whatever budget it had left.</summary>
    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The operator who cut the render.</summary>
    public OperatorId CreatedBy { get; }

    /// <summary>Set when the credential was revoked through this row; null while it lives.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    public Launcher(
        LauncherId id,
        EngagementId engagementId,
        Guid payloadId,
        Guid listenerId,
        string frontName,
        string frontEndpoint,
        StagerTokenId tokenId,
        string tokenSecret,
        string url,
        int maxUses,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt,
        OperatorId createdBy)
    {
        Id = id;
        EngagementId = engagementId;
        PayloadId = payloadId;
        ListenerId = listenerId;
        FrontName = frontName;
        FrontEndpoint = frontEndpoint;
        TokenId = tokenId;
        TokenSecret = tokenSecret;
        Url = url;
        MaxUses = maxUses;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
        CreatedBy = createdBy;
    }

    /// <summary>
    /// Marks the row's credential revoked. Bookkeeping only -- the revocation
    /// itself is the token store's <c>RevokeAsync</c>, called by the endpoint
    /// beside this; the row records when the operator pulled the handle.
    /// Idempotent: a second call changes nothing and returns false.
    /// </summary>
    public bool Revoke(DateTimeOffset at)
    {
        if (RevokedAt is not null)
            return false;

        RevokedAt = at;
        return true;
    }
}
