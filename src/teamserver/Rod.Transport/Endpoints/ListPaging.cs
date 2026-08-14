namespace Rod.Transport.Endpoints;

/// <summary>
/// Shared list-pagination binding for the operator-facing listing endpoints
/// (architecture.md Sec 4.3, Sec 10.3/11): every listing accepts an optional
/// <c>limit</c> and an opaque <c>cursor</c>, and a long engagement no longer
/// grows the response without bound. The default page is 50 items; anything
/// above 200 is refused, and a cursor the listing's codec cannot decode is a
/// 400 rather than a silently restarted walk.
/// </summary>
internal static class ListPaging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>
    /// Binds the query parameters. Returns false with <paramref name="error"/>
    /// set when the limit is out of range or the cursor fails
    /// <paramref name="isValidCursor"/>.
    /// </summary>
    public static bool TryBind(
        int? limit,
        string? cursor,
        Func<string, bool> isValidCursor,
        out int boundLimit,
        out string? boundCursor,
        out string error)
    {
        boundLimit = limit ?? DefaultLimit;
        boundCursor = cursor;
        error = string.Empty;

        if (boundLimit < 1 || boundLimit > MaxLimit)
        {
            error = $"limit must be between 1 and {MaxLimit}.";
            return false;
        }

        if (cursor is not null && !isValidCursor(cursor))
        {
            error = "cursor is not a valid page cursor for this listing.";
            return false;
        }

        return true;
    }
}
