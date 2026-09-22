using System.Text;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// The baked profile's trust key (architecture.md Sec 9): which TLS roots
/// the artifact's dials accept. Pinned is the posture every default bake
/// carries; public is the explicit real-domain departure.
/// </summary>
public class ProfileBakeTests
{
    [Theory]
    [InlineData(TlsTrust.Pinned, "pinned")]
    [InlineData(TlsTrust.Public, "public")]
    public void TheBakeCarriesTheTlsTrustPosture(TlsTrust trust, string wire)
    {
        var json = Encoding.UTF8.GetString(Base64Url.Decode(Bake(trust)));
        Assert.Contains($"\"tlsTrust\":\"{wire}\"", json);
    }

    private static string Bake(TlsTrust trust)
        => ProfileBake.Render(new BuildParams(
            new EngagementId(Guid.NewGuid()),
            new OperatorId(Guid.NewGuid()),
            ImplantClass.Stage2,
            new TargetProfile("linux", "amd64"),
            new TransportProfile("https://front.example", "/beacon") { TlsTrust = trust },
            new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), KillDate: null)));
}
