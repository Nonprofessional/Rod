namespace Rod.CoreState.Collaboration;

/// <summary>
/// One page of a newest-window paged read over the collaboration stores.
/// The items read oldest-first within the page (matching the audit
/// listing's page order); <see cref="HasMore"/> is exact. The page carries
/// the items only -- the cursor codec is the caller's: the timeline reads
/// (transport) encode the page's oldest key with the audit layer's
/// <c>TimestampIdCursor</c>, which CoreState cannot reference, so the store
/// windows take the decoded key instead of an opaque string.
/// </summary>
public sealed record NewestWindowPage<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>
/// The shared window for the in-memory collaboration stores: walks an
/// ascending-ordered array from the newest end, skipping keys at-or-newer
/// than the decoded (timestamp, id) key, and produces one page. The same
/// semantics the audit stores' window holds, over the same key shape, so
/// the merged timeline pages one keyset across every source.
/// </summary>
internal static class CollaborationWindow
{
    public static NewestWindowPage<T> TakeNewest<T>(
        T[] orderedAscending,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        Func<T, DateTimeOffset> timestamp,
        Func<T, Guid> id)
    {
        var taken = new List<T>(Math.Min(limit, orderedAscending.Length));
        var hasMore = false;
        for (var i = orderedAscending.Length - 1; i >= 0; i--)
        {
            var item = orderedAscending[i];
            if (before is { } at && beforeId is { } afterId)
            {
                var itemAt = timestamp(item);
                var itemId = id(item);
                // At-or-newer than the key: an earlier (newer) page already
                // carried it.
                if (itemAt > at || (itemAt == at && itemId.CompareTo(afterId) >= 0))
                    continue;
            }

            taken.Add(item);
            if (taken.Count == limit)
            {
                // Everything below the break is strictly older.
                hasMore = i > 0;
                break;
            }
        }

        taken.Reverse(); // Oldest first within the page.
        return new NewestWindowPage<T>(taken, hasMore);
    }
}
