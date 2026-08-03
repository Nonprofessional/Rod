using Rod.Transport;

// The teamserver composition root (architecture.md Sec 4, roadmap M1.5). This is
// the single runnable .NET process: it wires the transport layer's services and
// operator/implant endpoints, serves the built operator UI (Vite output under
// wwwroot), and falls any non-file, non-API request back to index.html so the
// client-side router owns deep links. UI <-> data goes over the HTTP API; the
// host itself adds no domain logic.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRodTransport();

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
