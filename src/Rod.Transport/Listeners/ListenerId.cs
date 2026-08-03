namespace Rod.Transport.Listeners;

/// <summary>
/// Identifies one listener -- a bound C2 ingress the teamserver terminates
/// (architecture.md Sec 8). Listeners are global teamserver infrastructure, not
/// engagement domain state, so this id lives in the transport layer rather than
/// alongside the engagement ids in core state.
/// </summary>
public readonly record struct ListenerId(Guid Value)
{
    public static ListenerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");

    /// <summary>
    /// Parses a listener id from its string form. Accepts both the compact "N"
    /// format produced by <see cref="ToString"/> and the hyphenated Guid form;
    /// returns false on anything else.
    /// </summary>
    public static bool TryParse(string? text, out ListenerId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new ListenerId(guid);
            return true;
        }

        id = default;
        return false;
    }
}
