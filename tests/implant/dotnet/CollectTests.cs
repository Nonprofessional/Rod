using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// CollectTests covers the collect.cred dispatch surface: source filtering
// plus the no-secret-material invariant. The AWS/SSH enumeration runs against
// a synthetic HOME so the test never touches the developer's own ~/.ssh or
// ~/.aws; cmdkey is Windows-only and its refusal is documented by the platform
// branch. The file-transfer verbs live in FileOpsTests.
public class CollectTests
{
    private static HandlerRegistry NewRegistry() => HandlerRegistry.Default();

    [Fact]
    public void CollectCred_UnknownSource_FailsWithCause()
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("collect.cred", "kerberos");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("unknown source", output);
    }

    [Fact]
    public void CollectCred_ListsSSHProfiles_NoSecretMaterial()
    {
        // The synthetic ~/.ssh fixture relies on POSIX HOME; the Windows build
        // exercises the cred path end-to-end instead.
        if (!OperatingSystem.IsLinux()) return;

        using var home = TempDir.Create();
        using (new EnvScope("HOME", home.Path))
        {
            Directory.CreateDirectory(Path.Combine(home.Path, ".ssh"));
            // A bare private key (no .pub sibling) so the "private key, no .pub"
            // line appears. The handler reads private-key presence by name only,
            // never the bytes, so the body is a recognizable canary.
            File.WriteAllText(Path.Combine(home.Path, ".ssh", "id_bare"),
                "-----BEGIN OPENSSH PRIVATE KEY-----\nFAKEKEYBODY_DO_NOT_LEAK\n-----END OPENSSH PRIVATE KEY-----\n");
            // A public key; the handler fingerprints it (or skips on a parse
            // failure). Either way the bare-key line proves the no-secret rule.
            File.WriteAllText(Path.Combine(home.Path, ".ssh", "id_ed25519.pub"),
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKdTestKeyRodCollectCredSSH collect-test\n");

            var registry = NewRegistry();
            var (outcome, output, _) = registry.Dispatch("collect.cred", "ssh");
            Assert.Equal(TaskOutcome.Succeeded, outcome);
            Assert.Contains("id_bare", output);
            Assert.Contains("no .pub sibling", output);
            // The private key body must never appear in the output.
            Assert.DoesNotContain("FAKEKEYBODY_DO_NOT_LEAK", output);
            Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", output);
        }
    }

    [Fact]
    public void CollectCred_ListsAWSProfiles_NoSecretMaterial()
    {
        if (!OperatingSystem.IsLinux()) return; // synthetic ~/.aws relies on POSIX HOME

        using var home = TempDir.Create();
        using (new EnvScope("HOME", home.Path))
        {
            Directory.CreateDirectory(Path.Combine(home.Path, ".aws"));
            File.WriteAllText(Path.Combine(home.Path, ".aws", "credentials"),
                "[default]\n" +
                "aws_access_key_id = AKIAFAKEKEYID1234\n" +
                "aws_secret_access_key = sUpErSeCrEtDoNoTlEaK1234567890\n" +
                "\n" +
                "[work]\n" +
                "aws_access_key_id = AKIAOTHERKEYID5678\n" +
                "aws_secret_access_key = aNoThErSeCrEtVaLuE0987654321\n");

            var registry = NewRegistry();
            var (outcome, output, _) = registry.Dispatch("collect.cred", "aws");
            Assert.Equal(TaskOutcome.Succeeded, outcome);
            Assert.Contains("aws default", output);
            Assert.Contains("aws work", output);
            Assert.Contains("secret in file", output);
            // No secret access key value is ever surfaced.
            Assert.DoesNotContain("sUpErSeCrEtDoNoTlEaK", output);
            Assert.DoesNotContain("aNoThErSeCrEtVaLuE", output);
        }
    }
}
