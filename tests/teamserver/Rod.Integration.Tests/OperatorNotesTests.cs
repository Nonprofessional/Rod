using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.Persistence;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: operator notes on implants -- the "whose beacon is this" memory.
/// A note added through the operator HTTP API is attributed to the writing
/// operator, recorded as an ImplantNoteAdded audit event, and read back on the
/// implant view; the restart test proves the note's only storage is the audit
/// trail, so it survives a teamserver restart with the trail itself
/// (architecture.md Sec 3, Sec 11).
/// </summary>
public sealed class OperatorNotesTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public OperatorNotesTests(PostgresFixture postgres)
        => _postgres = postgres;

    [Fact]
    public async Task Notes_AreAttributed_QueriedFromTheTrail_AndOldestFirst()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            var first = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/implants/{implantId}/notes",
                new ImplantEndpoints.AddNoteRequest(Text: "IT-WKS-04,Marketing dept, JBS's box"));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var firstNote = await first.Content.ReadFromJsonAsync<ImplantEndpoints.ImplantNoteResponse>();
            Assert.NotNull(firstNote);
            Assert.Equal(implantId, firstNote!.ImplantId);
            Assert.Equal(operatorId.ToString(), firstNote.Author);

            var second = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/implants/{implantId}/notes",
                new ImplantEndpoints.AddNoteRequest(Text: "burned once in Q2, watch EDR"));
            second.EnsureSuccessStatusCode();

            // The implant view reads its notes back oldest first -- a note's
            // only storage is the trail, so the listing is a query over it.
            var listed = await client.GetFromJsonAsync<ImplantEndpoints.ImplantNoteResponse[]>(
                $"/engagements/{engagementId}/implants/{implantId}/notes");
            Assert.NotNull(listed);
            Assert.Equal(2, listed!.Length);
            Assert.Equal("IT-WKS-04,Marketing dept, JBS's box", listed[0].Text);
            Assert.Equal("burned once in Q2, watch EDR", listed[1].Text);
            Assert.Equal(operatorId.ToString(), listed[0].Author);

            // The note landed in the engagement trail as an ImplantNoteAdded
            // event: attributed to the writer, bound to the implant, the note
            // text as the payload -- part of the timeline like every operator
            // action (architecture.md Sec 11).
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            var noteEvents = trail.Where(e => e.Kind == AuditEventKind.ImplantNoteAdded).ToArray();
            Assert.Equal(2, noteEvents.Length);
            Assert.All(noteEvents, e => Assert.Equal(Guid.Parse(implantId), e.ImplantId));
            Assert.All(noteEvents, e => Assert.Equal(operatorId.Value, e.OperatorId));
            Assert.Contains(noteEvents, e => e.Payload == "burned once in Q2, watch EDR");
        }
    }

    [Fact]
    public async Task AddNote_ValidatesTheRequest()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var otherEngagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            // Blank text is malformed.
            var blank = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/implants/{implantId}/notes",
                new ImplantEndpoints.AddNoteRequest(Text: "  "));
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

            // A note is a sentence or three, not a paste target.
            var oversized = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/implants/{implantId}/notes",
                new ImplantEndpoints.AddNoteRequest(Text: new string('x', 8 * 1024 + 1)));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);

            // An unknown implant is a routing failure.
            var unknown = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/implants/{Guid.NewGuid()}/notes",
                new ImplantEndpoints.AddNoteRequest(Text: "note"));
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            // The implant exists, but not in this engagement -- cross-engagement
            // access is impossible by construction (architecture.md Sec 3).
            var foreign = await client.PostAsJsonAsync(
                $"/engagements/{otherEngagementId}/implants/{implantId}/notes",
                new ImplantEndpoints.AddNoteRequest(Text: "note"));
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

            // Nothing from the refused posts landed in the trail.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            Assert.DoesNotContain(
                await audit.ListAsync(Guid.Parse(engagementId)),
                e => e.Kind == AuditEventKind.ImplantNoteAdded);
        }
    }

    [Fact]
    public async Task Notes_SurviveRestart_WhenPostgresWired()
    {
        if (!_postgres.IsAvailable)
        {
            // No Docker in this environment; skip, not fail.
            return;
        }

        // --- Host A: the running operation. The engagement, implant, and note
        //     all land in Postgres (the note as an audit event). ---
        string engagementId;
        string implantId;
        Guid noteId;

        await using (var envA = await TestEnv.StartAsync(_postgres.ConnectionString))
        {
            engagementId = await CreateEngagementAsync(envA.Http);
            var secret = await MintStagerTokenAsync(envA.Http, engagementId);
            implantId = await EnrollAsync(envA.Http, secret);

            var added = await envA.Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/implants/{implantId}/notes",
                new ImplantEndpoints.AddNoteRequest(Text: "HVXC-web-03, edge web tier"));
            added.EnsureSuccessStatusCode();
            var note = await added.Content.ReadFromJsonAsync<ImplantEndpoints.ImplantNoteResponse>();
            noteId = Guid.Parse(note!.NoteId);
        }
        // Host A is disposed: process, listeners, and caches are gone. Only the
        // durable adapters' rows remain -- the note among them, as an audit
        // event.

        // --- Host B: a fresh teamserver over the same Postgres. The implant
        //     still exists (the durable implant store came back) and its notes
        //     still read -- the note rode the audit trail across the restart.
        //     This is the AC: a note added in the client survives a teamserver
        //     restart via the audit store and shows on the implant view. ---
        await using var envB = await TestEnv.StartAsync(_postgres.ConnectionString);

        var listed = await envB.Http.GetFromJsonAsync<ImplantEndpoints.ImplantNoteResponse[]>(
            $"/engagements/{engagementId}/implants/{implantId}/notes");
        var survived = Assert.Single(listed!);
        Assert.Equal(noteId.ToString(), survived.NoteId);
        Assert.Equal("HVXC-web-03, edge web tier", survived.Text);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> MintStagerTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        return token!.Secret;
    }

    private static async Task<string> EnrollAsync(HttpClient client, string secret)
    {
        var response = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null, PublicKey: null));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        return enrolled!.ImplantId!;
    }

    /// <summary>
    /// A real Kestrel teamserver over the Postgres-backed durable adapters
    /// (the same host shape CoreStateDurabilityTests uses): two instances over
    /// one database exercise restart-and-recover.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public OperatorId OperatorId { get; private set; }

        public static async Task<TestEnv> StartAsync(string connectionString)
        {
            var env = new TestEnv();
            var httpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig(
                extend: dict => dict["ConnectionStrings:Postgres"] = connectionString);

            env.Host = TransportHost.CreateHostBuilder(
                    configuration: config,
                    configureServices: services => AuthenticatedHost.ComposeServices(
                        services, config, extra: s => s.AddRodPersistence(config)),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints))
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(httpPort)))
                .Build();

            // The schema must exist before the hosted services start (the
            // operator bootstrap seed writes its row at startup).
            var factory = env.Host.Services.GetRequiredService<IDbContextFactory<RodPersistenceDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            await env.Host.StartAsync();
            env.OperatorId = AuthenticatedHost.GetOperatorId(env.Host);

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler { UseProxy = false }))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
