using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rod.Transport.Redirectors;

/// <summary>
/// The control channel's message grammar (architecture.md Sec 8): a small,
/// deliberately dumb JSON-over-WebSocket protocol -- enroll once against a
/// pre-provisioned credential, reconnect with the minted node secret,
/// heartbeat, and receive full-state rule pushes. It is not a second wire
/// protocol and grows no capability surface of its own; every field here
/// names fleet plumbing, never engagement data.
/// </summary>
public static class RedirectorControlMessages
{
    // Node -> server.
    public const string Enroll = "enroll";
    public const string Hello = "hello";
    public const string Heartbeat = "heartbeat";
    public const string Ack = "ack";

    // Server -> node.
    public const string Enrolled = "enrolled";
    public const string Welcome = "welcome";
    public const string Rules = "rules";
    public const string Refused = "refused";
}

/// <summary>Shared JSON defaults for the control channel.</summary>
internal static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Any message on the control channel, discriminated by <c>type</c>.</summary>
internal sealed record ControlMessage(
    [property: JsonPropertyName("type")] string Type,
    string? Credential = null,
    string? NodeId = null,
    string? NodeSecret = null,
    string? Version = null,
    string? Commit = null,
    string? Digest = null,
    RedirectorRuleSet? Rules = null,
    string? Reason = null);

/// <summary>Factory for the messages each side sends.</summary>
internal static class ControlMessages
{
    public static ControlMessage EnrollRequest(string credential, string? version, string? commit)
        => new(RedirectorControlMessages.Enroll, Credential: credential, Version: version, Commit: commit);

    public static ControlMessage HelloRequest(string nodeId, string nodeSecret, string? version, string? commit)
        => new(RedirectorControlMessages.Hello, NodeId: nodeId, NodeSecret: nodeSecret, Version: version, Commit: commit);

    public static ControlMessage Heartbeat(string? version, string? commit)
        => new(RedirectorControlMessages.Heartbeat, Version: version, Commit: commit);

    public static ControlMessage Ack(string digest)
        => new(RedirectorControlMessages.Ack, Digest: digest);

    public static ControlMessage EnrolledResult(string nodeId, string nodeSecret, RedirectorRuleSet rules)
        => new(RedirectorControlMessages.Enrolled, NodeId: nodeId, NodeSecret: nodeSecret, Rules: rules);

    public static ControlMessage WelcomeResult(RedirectorRuleSet rules)
        => new(RedirectorControlMessages.Welcome, Rules: rules);

    public static ControlMessage RulesPush(RedirectorRuleSet rules)
        => new(RedirectorControlMessages.Rules, Rules: rules);

    public static ControlMessage Refused(string reason)
        => new(RedirectorControlMessages.Refused, Reason: reason);
}
