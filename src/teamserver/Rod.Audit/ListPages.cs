using System.Text;

namespace Rod.Audit;

/// <summary>
/// One page of a paged audit-trail listing (architecture.md Sec 11): the newest
/// window first (a null cursor), <see cref="NextCursor"/> walking one page
/// older until it is null at the beginning of the trail. Items within a page
/// read oldest first, matching the full listing's order.
/// </summary>
public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, string? NextCursor);

/// <summary>
/// One page of a paged artifact listing, with the same newest-window-first
/// cursor semantics as <see cref="AuditPage"/>.
/// </summary>
public sealed record ArtifactPage(IReadOnlyList<Artifact> Items, string? NextCursor);

/// <summary>
/// The opaque page-cursor codec for audit and artifact listings. A cursor
/// encodes the (timestamp, id) key of the oldest record on the page it was
/// returned with -- the timestamp as UTC ticks, the id as its hex form -- so
/// the next page is "everything strictly older than that key". The wire form is
/// base64url text, deliberately opaque: clients treat it as an atom.
/// </summary>
public static class TimestampIdCursor
{
    public static string Encode(DateTimeOffset at, Guid id)
        => Base64Url(Encoding.UTF8.GetBytes($"{at.UtcTicks}:{id:N}"));

    /// <summary>True when <paramref name="cursor"/> decodes to a (ticks, id) key.</summary>
    public static bool TryDecode(string? cursor, out long utcTicks, out Guid id)
    {
        utcTicks = 0;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(cursor))
            return false;

        try
        {
            var text = Encoding.UTF8.GetString(FromBase64Url(cursor));
            var colon = text.IndexOf(':');
            if (colon <= 0
                || !long.TryParse(text.AsSpan(0, colon), out utcTicks)
                || !Guid.TryParseExact(text.AsSpan(colon + 1), "N", out id))
            {
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // True when (aTicks, aId) sorts at-or-newer than (bTicks, bId). The page
    // window walks from the newest end, so a cursor skips every key this
    // returns true for.
    public static bool AtOrNewer(long aTicks, Guid aId, long bTicks, Guid bId)
        => aTicks != bTicks ? aTicks > bTicks : aId.CompareTo(bId) >= 0;

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// The shared in-process page window for the in-memory and file adapters:
/// walks an ascending-ordered array from the newest end, skipping keys
/// at-or-newer than the cursor, and produces one page plus the next cursor.
/// The Postgres adapter runs the same window as a keyset query.
/// </summary>
internal static class ListPageWindow
{
    public static (IReadOnlyList<T> Items, string? NextCursor) TakeNewest<T>(
        T[] orderedAscending,
        int limit,
        string? cursor,
        Func<T, DateTimeOffset> timestamp,
        Func<T, Guid> id)
    {
        long? afterTicks = null;
        Guid afterId = Guid.Empty;
        if (cursor is not null)
        {
            if (!TimestampIdCursor.TryDecode(cursor, out var decodedTicks, out var decodedId))
                throw new ArgumentException("Cursor is not a valid list page cursor.", nameof(cursor));
            afterTicks = decodedTicks;
            afterId = decodedId;
        }

        var taken = new List<T>(limit);
        var hasOlder = false;
        for (var i = orderedAscending.Length - 1; i >= 0; i--)
        {
            var item = orderedAscending[i];
            if (afterTicks is { } at
                && TimestampIdCursor.AtOrNewer(timestamp(item).UtcTicks, id(item), at, afterId))
            {
                continue; // Already on an earlier (newer) page.
            }

            taken.Add(item);
            if (taken.Count == limit)
            {
                hasOlder = i > 0;
                break;
            }
        }

        taken.Reverse(); // Oldest first within the page, matching the full listing.
        var next = hasOlder
            ? TimestampIdCursor.Encode(timestamp(taken[0]), id(taken[0]))
            : null;
        return (taken, next);
    }
}
