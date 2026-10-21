using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Rod.Transport;

/// <summary>
/// Shapes the generated OpenAPI document into the versioned operator API's
/// contract (architecture.md Sec 15.3, ADR-006): the document describes the
/// operator surface only, addressed through the <c>/api/v1</c> alias, so the
/// spec an SDK consumer reads is exactly the space ADR-006 versions. The
/// wire-facing surfaces stay out of the contract on purpose -- implants,
/// redirector nodes, and the public hook/lure edges speak the wire protocol
/// and the serving-edge grammar (Sec 8), not this API, and advertising their
/// routes to API consumers would be noise at best.
/// </summary>
internal sealed class OperatorApiDocumentShaping : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Title = "Rod teamserver operator API";
        document.Info.Version = "v1";
        document.Info.Description =
            "The engagement-scoped operator control plane: engagements, tasking, "
            + "evidence, and the collaboration surfaces. Cookie sessions and bearer "
            + "API tokens authenticate; the base URL is the versioned /api/v1 alias.";

        // The versioned alias (OperatorApiVersionAlias) is the address this
        // contract versions, so it is the document's server base URL: the paths
        // below resolve against /api/v1 and nowhere else.
        document.Servers = [new OpenApiServer { Url = OperatorApiVersionAlias.Prefix }];

        // Drop every route that is not operator API. The exclusions are the
        // implant ingress (enrollment, the beacon carriages, the payload,
        // loader, hook, and lure fetch edges), the DNS-over-HTTPS resolver
        // front, the redirector control channel, the MCP server (its own
        // protocol surface under ADR-006's later scope work), the health
        // probe, and the SPA root.
        var excluded = document.Paths.Keys
            .Where(IsWireFacing)
            .ToList();
        foreach (var path in excluded)
            document.Paths.Remove(path);

        return Task.CompletedTask;
    }

    private static bool IsWireFacing(string path)
        => path == "/"
            || path == "/health"
            || path == "/mcp"
            || path == "/dns-query"
            || path == "/redirectors/control"
            || path.StartsWith("/implants/", StringComparison.Ordinal);
}
