namespace Rod.Operators.Llm;

/// <summary>
/// Configuration for the LLM triage and reporting client (architecture.md
/// Sec 11). Opt-in by construction: the integration is disabled until an
/// operator names an endpoint, a key, and a model, because every request
/// sends engagement content off-platform -- the egress decision is the
/// operator's, documented in the operations runbook, never a silent default.
/// The wire contract is the OpenAI chat-completions shape, not a vendor: any
/// compatible endpoint (cloud or a local runtime) serves it.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Master switch. Off until deliberately turned on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The compatible endpoint's base URL, e.g. <c>https://host/v4</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The bearer key the endpoint authenticates. Never enters the audit trail.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The model name the endpoint serves.</summary>
    public string? Model { get; set; }

    /// <summary>Per-request budget. Reasoning models answer slowly; the default is generous.</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// How much captured task output a single request may carry. Longer
    /// transcripts are truncated with a marker, the listing-paging posture:
    /// one request stays bounded.
    /// </summary>
    public int MaxInputChars { get; set; } = 65536;

    /// <summary>The response budget. Reasoning models spend tokens before answering.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>True when the integration is both enabled and fully named.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Model);
}
