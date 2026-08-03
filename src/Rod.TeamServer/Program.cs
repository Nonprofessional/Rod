using Rod.Transport;
using Rod.Transport.Listeners;

// The teamserver composition root (architecture.md Sec 4, roadmap M1.5). This is
// the single runnable .NET process: it wires the transport layer's services and
// operator/implant endpoints, binds the configured listeners (roadmap M2.2), serves
// the built operator UI (Vite output under wwwroot), and falls any non-file,
// non-API request back to index.html so the client-side router owns deep links. UI
// <-> data goes over the HTTP API; the host itself adds no domain logic.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRodTransport();

// Bind the configured listeners (roadmap M2.2, architecture.md Sec 8). Each entry
// is one C2 ingress: a transport (HTTP(S) or mTLS), the address Kestrel opens, and
// the public endpoint implants dial -- decoupled, so a burned redirector is
// replaceable without backend change. When the section is absent the host falls
// back to a single loopback HTTP listener so `dotnet run` still works out of the
// box.
var listenerConfigs = builder.Configuration.GetSection("Listeners").Get<List<ListenerConfig>>();
if (listenerConfigs is null || listenerConfigs.Count == 0)
{
    listenerConfigs = new List<ListenerConfig>
    {
        new("dev-http", ListenerTransport.Http, "127.0.0.1:5080", "http://localhost:5080"),
    };
}
builder.WebHost.UseRodListeners(listenerConfigs);

var app = builder.Build();

app.UseStaticFiles();
app.MapRodEndpoints();

// SPA fallback: anything not handled by a static file or an API route returns
// the React shell, so client-side routing owns deep links (e.g. /engagements/..).
app.MapGet("/{**slug}", async context =>
{
    // Don't shadow API or health routes that registered above; MapRodEndpoints
    // already claimed those, so this only runs for unmatched paths.
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/index.html");
});

app.Run();
