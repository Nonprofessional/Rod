using Microsoft.EntityFrameworkCore;
using Rod.Audit;
using Rod.Operators;
using Rod.Operators.Auth;
using Rod.Persistence;
using Rod.TeamServer;
using Rod.Tradecraft;
using Rod.Transport;
using Rod.Transport.Listeners;

// The offline evidence verifier (architecture.md Sec 11): `--verify-evidence
// <package.zip>` checks a closed engagement's exported package on a host with
// no Rod infrastructure running. Handled before the host is built -- the
// verifier needs no listeners, no stores, and no configuration.
if (args is ["--verify-evidence", var packagePath])
{
    return await EvidenceVerifier.RunAsync(packagePath);
}

// The teamserver composition root (architecture.md Sec 4). This is
// the single runnable .NET process: it wires the transport layer's services and
// operator/implant endpoints, binds the configured listeners, serves
// the built operator UI (Vite output under wwwroot), and falls any non-file,
// non-API request back to index.html so the client-side router owns deep links. UI
// <-> data goes over the HTTP API; the host itself adds no domain logic.

var builder = WebApplication.CreateBuilder(args);
// Pass configuration through so the composition root can select the durable
// audit/artifact stores when the Audit:DataDirectory section is present
//  -- the engagement trail outlives a restart and infrastructure teardown.
// Absent, the in-memory adapters stay in place.
builder.Services.AddRodTransport(builder.Configuration);
// Layer in the operator layer: the live-event bus that fans task
// and presence events out to connected operator sessions, plus the presence
// roster. Transport cannot reference Rod.Operators (architecture test
// LayerDependencyTests), so the composition root assembles it here.
builder.Services.AddRodOperators();
// Wire the tradecraft layer onto the live task path (architecture.md Sec 10.3):
// the capability registry and the registry-backed task resolver that replaces
// core state's strict class-table default, plus any out-of-tree capability
// modules listed under Tradecraft:Modules (Sec 10.2) -- each replaces the
// placeholder for its verb, so adding one never edits this composition root.
// Transport cannot reference Rod.Tradecraft (architecture test
// LayerDependencyTests), so the composition root assembles it here -- the same
// reason as AddRodOperators.
builder.Services.AddRodTradecraft(builder.Configuration);
// Wire the durable PostgreSQL store (architecture.md Sec 12, ADR 0003):
// when ConnectionStrings:Postgres is set, this registers the EF Core
// DbContext and replaces the in-memory core-state ports whose durable adapters
// are implemented with the Postgres-backed pair, so state survives a restart.
// Rod.Persistence cannot be referenced by Rod.Transport (architecture test
// LayerDependencyTests), so the composition root assembles it here -- the same
// reason as AddRodOperators/AddRodTradecraft above. Absent the connection
// string, it registers nothing and the in-memory adapters stay in place.
builder.Services.AddRodPersistence(builder.Configuration);
// Layer in operator authentication (architecture.md Sec 4, the production-
// hardening todo): the cookie session scheme, the password hasher, the login
// service, and the bootstrap account seed that provisions the first operator.
// Transport cannot reference Rod.Operators (architecture test
// LayerDependencyTests), so the composition root assembles it here -- the same
// reason as AddRodOperators above.
builder.Services.AddRodOperatorAuth(builder.Configuration);

// Bind the operator front (architecture.md Sec 8). Configuration names the
// shared tier only -- the loopback socket the operator UI and API ride (and
// any deliberately shared ingress a deployment fronts); the implant-facing
// listeners are engagement-scoped, created through the operator API, and
// persisted so a restart rebinds them. When the section is absent the host
// falls back to a single loopback listener so `dotnet run` still works out of
// the box.
var listenerConfigs = builder.Configuration.GetSection("Listeners").Get<List<ListenerConfig>>();
if (listenerConfigs is null || listenerConfigs.Count == 0)
{
    listenerConfigs = new List<ListenerConfig>
    {
        new("operator-http", "http", "127.0.0.1:5080", "http://localhost:5080"),
    };
}
builder.WebHost.UseRodListeners(listenerConfigs);

var app = builder.Build();

// Self-report provenance: the startup line names the exact source tree this
// binary was built from, so an installed teamserver introduces itself by commit
// instead of a hand-declared source path in configuration. GET /build reports
// the same pair on request (BuildStampEndpoints).
app.Logger.LogInformation(
    "Rod teamserver {Version} (commit {Commit})", BuildStamp.Version, BuildStamp.SourceCommit);

// The durable store's schema must match the model before any traffic is
// served: a database restored without its constraints boots apparently healthy
// and fails at the first task dispatch instead. The same fail-loudly posture
// as the CA and listener validation, run once per boot (Rod.Persistence owns
// the Postgres knowledge; the composition root just pulls the trigger).
if (app.Services.GetService<IDbContextFactory<RodPersistenceDbContext>>() is { } persistenceFactory)
{
    await PostgresSchemaGuard.VerifyAsync(persistenceFactory);
}

// Defense-in-depth response headers (architecture.md Sec 9). The operator UI
// renders implant-controlled strings (task output, audit payloads), so a strict
// CSP backs React's escaping: no inline scripts or styles exist in the bundle,
// which keeps the policy tight. Embedding and MIME-sniffing controls round it
// out. Applied to every response, including the API, so a stray HTML-shaped API
// response is still covered.
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});

// Serve the built operator UI (Vite output under wwwroot). index.html is
// no-cache because it references hashed asset names and must be fresh after a
// rebuild; the hashed /assets/* files are immutable and cache for a year.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = staticContext =>
    {
        var headers = staticContext.Context.Response.Headers;
        if (string.Equals(staticContext.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-cache";
        }
        else if (staticContext.File.PhysicalPath is { } path &&
                 path.Contains($"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}"))
        {
            headers.CacheControl = "public,max-age=31536000,immutable";
        }
    },
});
// Operator session middleware: authentication establishes the operator from the
// cookie, authorization gates the endpoints that opt in via RequireAuthorization.
// Ordered before endpoint mapping so the auth result is visible to every mapped
// route; the static UI shell above it stays reachable anonymously.
app.UseAuthentication();
app.UseAuthorization();
app.MapRodEndpoints();
// The operator layer's SSE event stream: mapped alongside the
// transport endpoints from the composition root for the same layer-separation
// reason as AddRodOperators above.
app.MapOperatorEndpoints();
// The operator session endpoints (login/logout/me): mapped alongside the other
// operator-layer endpoints from the composition root for the same layer-
// separation reason as AddRodOperators above.
app.MapOperatorAuthEndpoints();
// The build-report endpoint (GET /build): the binary's own provenance, mapped
// from the composition root because the stamp is a property of this executable,
// not of any layer.
app.MapBuildStampEndpoints();
// The tradecraft layer's capability catalog: mapped from the
// composition root for the same layer-separation reason as AddRodTradecraft --
// transport cannot reference Rod.Tradecraft, so the catalog endpoint is exposed
// from the layer that owns the registry. Lets the operator UI surface every
// capability category as tasking from the registry rather than a hardcoded verb
// table.
app.MapCapabilityEndpoints();

// The operator UI shell. The SPA is hash-routed, so the browser only ever
// requests the root from the server -- there is no catch-all fallback that
// would turn unknown API paths into a 200 HTML response. When the bundle is
// missing (a backend-only checkout without Node), say so plainly instead of
// failing at request time with a file error.
app.MapGet("/", async context =>
{
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync(
            "Operator UI bundle not found. Build it once with " +
            "`npm ci && npm run build` " +
            "in src/teamserver/Rod.TeamServer/Client (Node.js required), then restart the teamserver.");
        return;
    }
    context.Response.ContentType = "text/html";
    context.Response.Headers.CacheControl = "no-cache";
    await using var stream = index.CreateReadStream();
    await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
});

app.Run();
return 0;
