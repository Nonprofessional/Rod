using Rod.Implant.Internal;

namespace Rod.Implant.Tests;

// Pins the egress walk (architecture.md Sec 8): the baked endpoint list is the
// primary plus its fallbacks, the primary's beacon host may be explicit while
// fallbacks derive theirs, and a failed attempt advances the cursor with
// wrap-around -- the walk enroll retries and failed beacon cycles both ride.
public class EgressEndpointsTests
{
    [Fact]
    public void Of_ComposesThePrimaryPlusFallbacks_WithBeaconDerivationPerEntry()
    {
        // The explicit beacon host pairs with the primary only (the dev shape
        // where enroll and beacon hosts differ); every fallback derives its
        // beacon host from its own enroll URL -- the single-front deployment
        // shape the bake targets.
        var config = new Config
        {
            EnrollURL = "https://primary.example.test/implants/enroll",
            BeaconURL = "10.0.0.1:8443",
            FallbackEnrollURLs = new[]
            {
                "https://alt1.example.test/implants/enroll",
                "https://alt2.example.test",
            },
        };

        var egress = EgressEndpoints.Of(config);

        Assert.Equal(3, egress.Count);
        Assert.Equal("https://primary.example.test/implants/enroll", egress.CurrentEnrollUrl);
        Assert.Equal("10.0.0.1:8443", egress.CurrentBeaconUrl);
        egress.Advance();
        Assert.Equal("https://alt1.example.test/implants/enroll", egress.CurrentEnrollUrl);
        Assert.Equal("https://alt1.example.test", egress.CurrentBeaconUrl);
        egress.Advance();
        Assert.Equal("https://alt2.example.test", egress.CurrentBeaconUrl);
        // The walk wraps to the primary, keeping the whole list in rotation.
        egress.Advance();
        Assert.Equal(0, egress.Index);
        Assert.Equal("https://primary.example.test/implants/enroll", egress.CurrentEnrollUrl);
    }

    [Fact]
    public void Of_DerivesThePrimaryBeaconWhenNotExplicit()
    {
        var config = new Config
        {
            EnrollURL = "https://primary.example.test/implants/enroll",
            StagerToken = "t",
        };

        var egress = EgressEndpoints.Of(config);

        Assert.Equal(1, egress.Count);
        Assert.Equal("https://primary.example.test", egress.CurrentBeaconUrl);
        // A single-entry walk has nowhere to go; Advance stays put.
        egress.Advance();
        Assert.Equal(0, egress.Index);
    }
}
