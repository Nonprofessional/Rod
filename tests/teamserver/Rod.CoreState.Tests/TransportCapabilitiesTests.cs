using Rod.CoreState.Tasks;
using Rod.CoreState.Transports;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the carrier capability table and the shared claim evaluation
/// (architecture.md Sec 10.3): which carriers may claim a channel task, the
/// conservative fallback for an undeclared carrier name, and the registry a
/// carrier beyond the in-tree four declares itself through.
/// </summary>
public class TransportCapabilitiesTests
{
    [Fact]
    public void EvaluateClaim_ClaimsOneShotTaskingOnAPollCarrier()
    {
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.Envelope, "shell.exec", wireSize: 100, maxBytes: 200);

        Assert.Equal(ClaimDecision.Claim, decision);
    }

    [Fact]
    public void EvaluateClaim_DefersAChannelVerbOnAPollCarrier()
    {
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.Envelope, ChannelVerbs.ShellInteract, wireSize: 100, maxBytes: 200);

        Assert.Equal(ClaimDecision.NeedsLiveChannel, decision);
    }

    [Fact]
    public void EvaluateClaim_ClaimsAChannelVerbOnADegradedCarrierWithTheOptIn()
    {
        // The store-and-forward discipline, admitted only when the implant
        // opted in (the session's advertisement the dispatch path passes).
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.Envelope, ChannelVerbs.ShellInteract,
            wireSize: 100, maxBytes: 200, degradedChannels: true);

        Assert.Equal(ClaimDecision.Claim, decision);
    }

    [Fact]
    public void EvaluateClaim_DefersAChannelVerbOnADegradedCarrierWithoutTheOptIn()
    {
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.MessagePipe, ChannelVerbs.TunnelSocks,
            wireSize: 100, maxBytes: 200, degradedChannels: false);

        Assert.Equal(ClaimDecision.NeedsLiveChannel, decision);
    }

    [Fact]
    public void EvaluateClaim_ANativeCarrierIgnoresTheDegradedOptIn()
    {
        // The opt-in only widens poll carriers; the native carrier's claim is
        // the same either way.
        var withoutOptIn = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.BeaconStream, ChannelVerbs.ShellInteract, wireSize: 100, maxBytes: 200);
        var withOptIn = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.BeaconStream, ChannelVerbs.ShellInteract,
            wireSize: 100, maxBytes: 200, degradedChannels: true);

        Assert.Equal(ClaimDecision.Claim, withoutOptIn);
        Assert.Equal(withoutOptIn, withOptIn);
    }

    [Fact]
    public void EvaluateClaim_ClaimsAChannelVerbOnTheNativeCarrier()
    {
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.BeaconStream, ChannelVerbs.TunnelSocks, wireSize: 100, maxBytes: 200);

        Assert.Equal(ClaimDecision.Claim, decision);
    }

    [Fact]
    public void EvaluateClaim_DefersAMarshaledTaskOverTheBudget()
    {
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.Envelope, "shell.exec", wireSize: 201, maxBytes: 200);

        Assert.Equal(ClaimDecision.Oversize, decision);
    }

    [Fact]
    public void EvaluateClaim_ClaimsAMarshaledTaskExactlyAtTheBudget()
    {
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.Envelope, "shell.exec", wireSize: 200, maxBytes: 200);

        Assert.Equal(ClaimDecision.Claim, decision);
    }

    [Fact]
    public void EvaluateClaim_TheChannelDeferralWinsOverSize()
    {
        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.Envelope, ChannelVerbs.TunnelForward, wireSize: 10_000, maxBytes: 200);

        Assert.Equal(ClaimDecision.NeedsLiveChannel, decision);
    }

    [Fact]
    public void Find_ReturnsTheDeclaredCarriersCaseInsensitively()
    {
        Assert.Same(TransportCapabilities.Envelope, TransportCapabilities.Find("ENVELOPE"));
        Assert.Same(TransportCapabilities.Dns, TransportCapabilities.Find("dns"));
        Assert.Same(TransportCapabilities.MessagePipe, TransportCapabilities.Find("message-pipe"));
        Assert.Same(TransportCapabilities.BeaconStream, TransportCapabilities.Find("beacon-stream"));
    }

    [Fact]
    public void Find_FallsBackToNoChannelSupportForAnUndeclaredCarrier()
    {
        var found = TransportCapabilities.Find("undeclared-test-carrier");

        Assert.Equal(ChannelSupport.None, found.Channels);
    }

    [Fact]
    public void Register_DeclaresACarrierTheCoreDoesNotKnow()
    {
        TransportCapabilities.Register(
            "registered-test-carrier", new CarrierCapabilities(ChannelSupport.None));

        var decision = TransportCapabilities.EvaluateClaim(
            TransportCapabilities.Find("registered-test-carrier"),
            ChannelVerbs.ShellInteract,
            wireSize: 10,
            maxBytes: 100);

        Assert.Equal(ClaimDecision.NeedsLiveChannel, decision);
    }

    [Fact]
    public void Register_RejectsAConflictingRedeclaration()
    {
        TransportCapabilities.Register(
            "conflicting-test-carrier", new CarrierCapabilities(ChannelSupport.None));

        Assert.Throws<InvalidOperationException>(() =>
            TransportCapabilities.Register(
                "CONFLICTING-TEST-CARRIER", new CarrierCapabilities(ChannelSupport.Native)));
    }

    [Fact]
    public void Register_TreatsAnIdenticalRedeclarationAsANoOp()
    {
        TransportCapabilities.Register(
            "idempotent-test-carrier", new CarrierCapabilities(ChannelSupport.None));

        // A second registration with the same value (a different instance):
        // harmless, so a double registration never throws.
        TransportCapabilities.Register(
            "idempotent-test-carrier", new CarrierCapabilities(ChannelSupport.None));

        Assert.Equal(
            ChannelSupport.None, TransportCapabilities.Find("idempotent-test-carrier").Channels);
    }
}
