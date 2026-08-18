using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the transform seam (architecture.md Sec 6, Sec 13): a
/// post-build transform chain driven by config, where an operator-supplied
/// out-of-tree transform wraps the built bytes and the platform records
/// exactly what happened. The config names the test assembly's own transform
/// type; a production transform is the same shape in an assembly placed next
/// to the teamserver binary. No in-tree transform ships -- the empty chain is
/// the seam -- and each transform owns its decode contract end to end.
/// The acceptance point: the config-listed transform runs in the build, and
/// the stored fingerprint plus the audit trail prove which transforms
/// produced the stored bytes.
/// </summary>
public class PayloadTransformChainTests
{
    // A transform built against the contract, listed by config. The exact
    // shape an operator-supplied out-of-tree assembly provides: it names
    // itself, sees the built bytes plus the build context, and returns
    // wrapped bytes plus a metadata note for the trail. It owns its own
    // decode contract -- the service generates no key material and knows
    // nothing about unwrapping (Sec 13).
    public sealed class ConfigListedWrapTransform : IPayloadTransform
    {
        public const string Marker = "RODWRAP1:";

        public string Name => "config-listed-wrap";

        public Task<PayloadTransformOutput> ApplyAsync(
            PayloadTransformInput input, CancellationToken cancellationToken = default)
            => Task.FromResult(new PayloadTransformOutput(
                Encoding.UTF8.GetBytes(Marker).Concat(input.Artifact).ToArray(),
                Metadata: $"wrapped {input.Artifact.Length} bytes for {input.Params.Target.OperatingSystem}"));
    }

    private const string TransformEntry =
        "Rod.Integration.Tests.PayloadTransformChainTests+ConfigListedWrapTransform, Rod.Integration.Tests";

    [DotNetFact]
    public async Task ConfigListedTransform_RunsInTheBuild_AndTheTrailProvesIt()
    {
        var (client, host, _) = AuthenticatedHost.Create(
            extendConfig: settings => settings["Build:Transforms:0"] = TransformEntry);
        using var hostScope = host;
        using var clientScope = client;
        await AuthenticatedHost.LoginAsync(client);

        // An engagement to build against.
        var create = await client.PostAsJsonAsync("/engagements", new { Name = "transform-seam" });
        create.EnsureSuccessStatusCode();
        var engagement = await create.Content.ReadFromJsonAsync<EngagementBody>();

        // The build runs the real .NET unit, then the config-listed transform
        // over its output.
        var build = await client.PostAsJsonAsync(
            $"/engagements/{engagement!.EngagementId}/payloads",
            new { TargetOs = "linux", TargetArch = "amd64" });
        build.EnsureSuccessStatusCode();
        var built = await build.Content.ReadFromJsonAsync<BuildBody>();
        Assert.NotNull(built);
        Assert.Contains("config-listed-wrap", built!.Transforms ?? Array.Empty<string>());

        // The stored bytes are the transformed bytes: the download carries
        // the transform's marker, and the recorded fingerprint is the
        // SHA-256 of exactly those bytes -- proof the trail describes what
        // ships.
        var download = await client.GetAsync($"/engagements/{engagement.EngagementId}/payloads/{built.ArtifactId}");
        download.EnsureSuccessStatusCode();
        var stored = await download.Content.ReadAsByteArrayAsync();
        Assert.StartsWith(ConfigListedWrapTransform.Marker, Encoding.UTF8.GetString(stored));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant(),
            built.Fingerprint);

        // The audit trail names the transform and its note: the PayloadBuilt
        // event is the durable answer to "which transforms produced the
        // stored bytes".
        var audit = host.Services.GetRequiredService<IAuditStore>();
        var trail = await audit.ListAsync(Guid.Parse(engagement.EngagementId));
        var payloadBuilt = trail.Single(e => e.Kind == AuditEventKind.PayloadBuilt);
        Assert.Contains("transforms=config-listed-wrap(", payloadBuilt.Payload);
        Assert.Contains("wrapped ", payloadBuilt.Payload);
        Assert.Equal(built.Fingerprint, payloadBuilt.Outcome);
    }

    private sealed class EngagementBody
    {
        public string EngagementId { get; set; } = "";
    }

    private sealed class BuildBody
    {
        public string ArtifactId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string[]? Transforms { get; set; }
    }
}
