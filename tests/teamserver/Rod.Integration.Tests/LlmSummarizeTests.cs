using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
// The domain entity shares its name with System.Threading.Tasks.Task and this
// file imports its namespace; the BCL type is what the handlers return, so it
// gets the short name and the entity stays fully qualified.
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// The LLM triage acceptance (architecture.md Sec 11, the
/// attention-compression surface): with the integration enabled against an
/// OpenAI-compatible endpoint, an operator summarizes a completed task's
/// output from the task read, and the request lands in the engagement's audit
/// trail. Disabled (the standing default) the route answers 503 naming the
/// configuration section, and a task without a final output is refused rather
/// than summarized half-run.
///
/// The endpoint is a stub loopback listener answering the chat-completions
/// shape with a fixed completion, so the egress contract is exercised without
/// network dependency.
/// </summary>
public class LlmSummarizeTests
{
    private const string SummaryText = "The command ran as the operator user; no further action needed.";

    /// <summary>
    /// Serves the chat-completions shape on an ephemeral loopback port until
    /// the returned stopper runs. Answers every POST with the fixed
    /// completion.
    /// </summary>
    private static (string BaseUrl, Action Stop) ServeStubEndpoint()
    {
        using var probe = TcpListener.Create(0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var stopped = new TaskCompletionSource();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (server.IsListening)
                {
                    var ctx = await server.GetContextAsync();
                    using var _ = ctx.Response;
                    var body = JsonSerializer.Serialize(new
                    {
                        id = "chatcmpl-stub",
                        @object = "chat.completion",
                        created = 0,
                        model = "stub-model",
                        choices = new[]
                        {
                            new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = SummaryText } },
                        },
                        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
                    });
                    var buffer = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = buffer.Length;
                    await ctx.Response.OutputStream.WriteAsync(buffer);
                }
            }
            catch (Exception) when (stopped.Task.IsCompleted)
            {
                // The stopper closed the listener mid-wait.
            }
        });
        return ($"http://127.0.0.1:{port}/v1", () =>
        {
            stopped.TrySetResult();
            server.Stop();
        }
        );
    }

    private static async Task<(Guid Engagement, Implant Implant, Rod.CoreState.Tasks.Task Completed, Rod.CoreState.Tasks.Task Queued)>
        SeedAsync(
            HttpClient client,
            IHost host,
            OperatorId operatorId)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Pressing Matter"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        var engagement = Guid.Parse(created!.EngagementId);

        var implant = await EngagementSetup.EnrollImplantAsync(host, new EngagementId(engagement));

        var tasks = host.Services.GetRequiredService<ITaskRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var completed = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), new EngagementId(engagement), implant.Id, operatorId,
            "shell.exec", "whoami", now);
        completed.MarkDispatched(now.AddSeconds(1));
        completed.Complete("operator\n", TaskOutcome.Succeeded, now.AddSeconds(2));
        await tasks.SaveAsync(completed);

        var queued = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), new EngagementId(engagement), implant.Id, operatorId,
            "shell.exec", "hostname", now.AddSeconds(3));
        await tasks.SaveAsync(queued);

        return (engagement, implant, completed, queued);
    }

    [Fact]
    public async Task Summarize_ServesTheAcceptance_AndRecordsTheRequestInTheTrail()
    {
        var (baseUrl, stop) = ServeStubEndpoint();
        try
        {
            var (client, host, operatorId) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Llm:Enabled"] = "true";
                settings["Llm:BaseUrl"] = baseUrl;
                settings["Llm:ApiKey"] = "stub-key";
                settings["Llm:Model"] = "stub-model";
                settings["Llm:RequestTimeoutSeconds"] = "30";
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var (engagement, implant, completed, queued) = await SeedAsync(client, host, operatorId);

                // The acceptance read: a completed task summarizes through the
                // configured endpoint.
                var response = await client.PostAsJsonAsync(
                    $"/engagements/{engagement}/tasks/{completed.Id.ToString()}:summarize", new { });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(SummaryText, body.GetProperty("summary").GetString());
                Assert.Equal("stub-model", body.GetProperty("model").GetString());

                // The request is in the trail -- the egress is the act.
                var audit = host.Services.GetRequiredService<IAuditStore>();
                var events = await audit.ListAsync(engagement);
                var summaryEvent = Assert.Single(events, e => e.Kind == AuditEventKind.LlmSummaryGenerated);
                Assert.Equal(completed.Id.Value, summaryEvent.TaskId);
                Assert.Equal(operatorId.Value, summaryEvent.OperatorId);
                Assert.Equal(SummaryText, summaryEvent.Output);
                Assert.StartsWith("succeeded:stub-model", summaryEvent.Outcome);

                // A task without a final output is refused, not half-summarized.
                var unfinished = await client.PostAsJsonAsync(
                    $"/engagements/{engagement}/tasks/{queued.Id.ToString()}:summarize", new { });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unfinished.StatusCode);
            }
        }
        finally
        {
            stop();
        }
    }

    [Fact]
    public async Task Summarize_IsRefusedWhileTheIntegrationIsDisabled()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (host)
        using (client)
        {
            await AuthenticatedHost.LoginAsync(client);
            var (engagement, implant, completed, queued) = await SeedAsync(client, host, operatorId);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagement}/tasks/{completed.Id.ToString()}:summarize", new { });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("Llm", problem.GetProperty("error").GetString());

            // Refusal leaves no egress event: nothing left the platform.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var events = await audit.ListAsync(engagement);
            Assert.DoesNotContain(events, e => e.Kind == AuditEventKind.LlmSummaryGenerated);
        }
    }

    // The live leg against a real compatible endpoint: same acceptance as the
    // stub, run only when the environment names one (ROD_LLM_LIVE_BASEURL and
    // ROD_LLM_LIVE_APIKEY), the Testcontainers-style machine gate -- CI and a
    // checkout without credentials skip it, an operator verifying their own
    // endpoint runs it.
    [Fact]
    public async Task Summarize_LiveEndpoint_WhenTheEnvironmentNamesOne()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ROD_LLM_LIVE_BASEURL");
        var apiKey = Environment.GetEnvironmentVariable("ROD_LLM_LIVE_APIKEY");
        var model = Environment.GetEnvironmentVariable("ROD_LLM_LIVE_MODEL") ?? "glm-5.3";
        if (baseUrl is null || apiKey is null)
            return;

        var (client, host, operatorId) = AuthenticatedHost.Create(extendConfig: settings =>
        {
            settings["Llm:Enabled"] = "true";
            settings["Llm:BaseUrl"] = baseUrl;
            settings["Llm:ApiKey"] = apiKey;
            settings["Llm:Model"] = model;
            settings["Llm:RequestTimeoutSeconds"] = "180";
        });
        using (host)
        using (client)
        {
            await AuthenticatedHost.LoginAsync(client);
            var (engagement, implant, completed, queued) = await SeedAsync(client, host, operatorId);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagement}/tasks/{completed.Id.ToString()}:summarize", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(model, body.GetProperty("model").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("summary").GetString()));

            var audit = host.Services.GetRequiredService<IAuditStore>();
            var events = await audit.ListAsync(engagement);
            Assert.Single(events, e => e.Kind == AuditEventKind.LlmSummaryGenerated);
        }
    }
}
