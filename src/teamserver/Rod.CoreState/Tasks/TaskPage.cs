using System.Text;
// The domain entity shares its name with System.Threading.Tasks.Task. Inside
// this namespace the entity wins; the BCL type is not needed here.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.CoreState.Tasks;

/// <summary>
/// One page of a paged task listing (architecture.md Sec 10.3, Sec 4.3). The
/// first page (a null cursor) is the newest <see cref="TaskPage.Items"/>-length
/// window of history; <see cref="NextCursor"/> walks one page older, until it is
/// null at the beginning of the history. Items within a page read oldest first,
/// matching the full listing's order.
/// </summary>
public sealed record TaskPage(IReadOnlyList<Task> Items, string? NextCursor);

/// <summary>
/// The opaque page-cursor codec for task listings. A cursor encodes the enqueue
/// sequence of the oldest task on the page it was returned with, so the next
/// page is "everything enqueued before that task". The wire form is base64url
/// text, deliberately opaque: clients treat it as an atom, never as a position.
/// </summary>
public static class TaskPageCursor
{
    private const string Prefix = "seq:";

    public static string Encode(long enqueueSequence)
        => Base64Url(Encoding.UTF8.GetBytes(Prefix + enqueueSequence));

    /// <summary>True when <paramref name="cursor"/> decodes to a sequence.</summary>
    public static bool TryDecode(string? cursor, out long enqueueSequence)
    {
        enqueueSequence = 0;
        if (string.IsNullOrEmpty(cursor))
            return false;

        try
        {
            var text = Encoding.UTF8.GetString(FromBase64Url(cursor));
            if (!text.StartsWith(Prefix, StringComparison.Ordinal)
                || !long.TryParse(text.AsSpan(Prefix.Length), out enqueueSequence))
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

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
