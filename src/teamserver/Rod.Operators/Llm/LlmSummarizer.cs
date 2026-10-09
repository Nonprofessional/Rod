using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Rod.Operators.Llm;

/// <summary>
/// The OpenAI-compatible chat-completions client behind
/// <c>Microsoft.Extensions.AI</c>'s <see cref="IChatClient"/>
/// (architecture.md Sec 11): one call shape -- summarize captured task output
/// for the operator who tasked it. The client is built lazily on the first
/// call (the webhook-pusher discipline: registering the service must not
/// activate anything in hosts that never call), and every call's outcome is
/// data for the trail, recorded by the endpoint that owns the audit write --
/// this type never throws for transport problems, it reports them.
/// </summary>
public sealed class LlmSummarizer
{
    /// <summary>
    /// The outcome of one summarization call: the summary text on success, or
    /// the readable refusal. The model rides both arms -- the trail's outcome
    /// names it either way.
    /// </summary>
    public sealed record Result(bool Succeeded, string? Summary, string Model, string? Reason);

    private readonly IOptions<LlmOptions> _options;
    private IChatClient? _client;

    public LlmSummarizer(IOptions<LlmOptions> options)
    {
        _options = options;
    }

    public bool Configured => _options.Value.IsConfigured;

    private IChatClient Client
    {
        get
        {
            if (_client is not null)
                return _client;
            var options = _options.Value;
            // The endpoint override is what makes the OpenAI client
            // OpenAI-compatible rather than OpenAI-only: the base URL names
            // whichever service answers the chat-completions shape. The
            // network timeout rides the client pipeline -- the per-request
            // budget the configured RequestTimeoutSeconds asks for.
            var openAi = new OpenAIClient(
                new ApiKeyCredential(options.ApiKey!),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(options.BaseUrl!),
                    NetworkTimeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)),
                });
            return _client = openAi.GetChatClient(options.Model!).AsIChatClient();
        }
    }

    /// <summary>
    /// Summarizes one task's captured output. The input is the task's own
    /// record -- verb, arguments, status, outcome, and the output transcript
    /// truncated to the configured input cap with a marker.
    /// </summary>
    public async Task<Result> SummarizeTaskAsync(Rod.CoreState.Tasks.Task task, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        try
        {
            var response = await Client.GetResponseAsync(
                BuildPrompt(task, options),
                new ChatOptions { MaxOutputTokens = options.MaxOutputTokens },
                cancellationToken);
            var summary = response.Text;
            return summary is { Length: > 0 }
                ? new Result(true, summary, options.Model!, null)
                : new Result(false, null, options.Model!, "The endpoint returned an empty response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Result(false, null, options.Model!, $"The request exceeded the {options.RequestTimeoutSeconds}s budget.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return new Result(false, null, options.Model!, FirstLine(ex.Message));
        }
    }

    private static string BuildPrompt(Rod.CoreState.Tasks.Task task, LlmOptions options)
    {
        var output = task.Output ?? string.Empty;
        if (output.Length > options.MaxInputChars)
            output = output[..options.MaxInputChars] + "\n...[truncated]";
        return $"""
            You are summarizing captured red-team engagement evidence for the operator who tasked it. Read the task record and produce a concise operational summary: what ran, what came back, and anything the operator should act on next. Plain text, no preamble.

            Task record:
            verb: {task.Verb}
            arguments: {task.Arguments}
            status: {task.Status}
            outcome: {task.Outcome}

            Captured output:
            {output}
            """;
    }

    private static string FirstLine(string text)
    {
        var separator = text.IndexOf('\n');
        return separator is > 0 and var stop ? text[..stop].Trim() : text.Trim();
    }
}
