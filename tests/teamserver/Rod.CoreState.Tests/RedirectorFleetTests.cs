using Rod.CoreState;
using Rod.CoreState.Redirectors;

namespace Rod.CoreState.Tests;

public class RedirectorFleetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static async Task<(RedirectorCredential Credential, EnrolledRedirector Enrolled)> EnrollAsync(
        IRedirectorFleet fleet, string name = "edge-fra-1")
    {
        var credential = await fleet.MintCredentialAsync(name, OperatorId.New(), Now);
        var enrolled = await fleet.EnrollAsync(credential.Secret, "203.0.113.10:51324", "1.0.0", "abc123", Now.AddMinutes(1));
        return (credential, enrolled);
    }

    [Fact]
    public async Task Enroll_ConsumesTheCredential()
    {
        var fleet = new InMemoryRedirectorFleet();
        var (credential, enrolled) = await EnrollAsync(fleet);

        Assert.Equal("edge-fra-1", enrolled.Node.Name);
        Assert.NotEqual(credential.Secret, enrolled.NodeSecret);
        Assert.Equal(Now.AddMinutes(1), enrolled.Node.EnrolledAt);

        var ledger = await fleet.ListCredentialsAsync();
        Assert.Equal(enrolled.Node.Id, ledger.Single().EnrolledNodeId);

        await Assert.ThrowsAsync<RedirectorEnrollException>(
            () => fleet.EnrollAsync(credential.Secret, null, null, null, Now.AddMinutes(2)));
    }

    [Fact]
    public async Task Enroll_UnknownSecretIsRefusedWithoutAnOracle()
    {
        var fleet = new InMemoryRedirectorFleet();

        var ex = await Assert.ThrowsAsync<RedirectorEnrollException>(
            () => fleet.EnrollAsync("not-a-secret", null, null, null, Now));

        Assert.Equal(RedirectorEnrollReason.Unknown, ex.Reason);
    }

    [Fact]
    public async Task Enroll_ExpiredCredentialIsRefused()
    {
        var fleet = new InMemoryRedirectorFleet();
        var credential = await fleet.MintCredentialAsync("edge", OperatorId.New(), Now, lifetime: TimeSpan.FromMinutes(10));

        var ex = await Assert.ThrowsAsync<RedirectorEnrollException>(
            () => fleet.EnrollAsync(credential.Secret, null, null, null, Now.AddMinutes(11)));

        Assert.Equal(RedirectorEnrollReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task Enroll_RevokedCredentialIsRefused()
    {
        var fleet = new InMemoryRedirectorFleet();
        var credential = await fleet.MintCredentialAsync("edge", OperatorId.New(), Now);

        await fleet.RevokeCredentialAsync(credential.Id, Now.AddMinutes(1));
        var ex = await Assert.ThrowsAsync<RedirectorEnrollException>(
            () => fleet.EnrollAsync(credential.Secret, null, null, null, Now.AddMinutes(2)));

        Assert.Equal(RedirectorEnrollReason.Revoked, ex.Reason);
    }

    [Fact]
    public async Task Revoke_IsIdempotentAndToleratesUnknownIds()
    {
        var fleet = new InMemoryRedirectorFleet();

        await fleet.RevokeCredentialAsync(RedirectorCredentialId.New(), Now);
        await fleet.RevokeCredentialAsync(RedirectorCredentialId.New(), Now);
    }

    [Fact]
    public async Task Authenticate_AcceptsTheNodeSecretAndRefusesEverythingElse()
    {
        var fleet = new InMemoryRedirectorFleet();
        var (_, enrolled) = await EnrollAsync(fleet);

        var node = await fleet.AuthenticateAsync(enrolled.Node.Id, enrolled.NodeSecret, Now.AddHours(1));
        Assert.Equal(Now.AddHours(1), node.LastSeenAt);

        // A wrong secret reads as unknown -- the reconnect path gets no oracle.
        await Assert.ThrowsAsync<RedirectorNodeRefusedException>(
            () => fleet.AuthenticateAsync(enrolled.Node.Id, "wrong", Now.AddHours(1)));
        await Assert.ThrowsAsync<RedirectorNodeRefusedException>(
            () => fleet.AuthenticateAsync(RedirectorNodeId.New(), enrolled.NodeSecret, Now.AddHours(1)));
    }

    [Fact]
    public async Task Retire_RefusesReconnectAndIsIdempotent()
    {
        var fleet = new InMemoryRedirectorFleet();
        var (_, enrolled) = await EnrollAsync(fleet);

        await fleet.RetireAsync(enrolled.Node.Id, Now.AddHours(1));
        await fleet.RetireAsync(enrolled.Node.Id, Now.AddHours(2));

        Assert.NotNull((await fleet.FindAsync(enrolled.Node.Id))!.RetiredAt);
        await Assert.ThrowsAsync<RedirectorNodeRefusedException>(
            () => fleet.AuthenticateAsync(enrolled.Node.Id, enrolled.NodeSecret, Now.AddHours(3)));
    }

    [Fact]
    public async Task Touch_ReportsContactAndIgnoresUnknownNodes()
    {
        var fleet = new InMemoryRedirectorFleet();
        var (_, enrolled) = await EnrollAsync(fleet);

        await fleet.TouchAsync(enrolled.Node.Id, "198.51.100.7:40000", "1.1.0", "def456", Now.AddHours(2));
        await fleet.TouchAsync(RedirectorNodeId.New(), null, null, null, Now.AddHours(2));

        var node = (await fleet.ListNodesAsync()).Single();
        Assert.Equal(Now.AddHours(2), node.LastSeenAt);
        Assert.Equal("198.51.100.7:40000", node.LastAddress);
        Assert.Equal("1.1.0", node.ReportedVersion);
        Assert.Equal("def456", node.ReportedCommit);
    }
}
