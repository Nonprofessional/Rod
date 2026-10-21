using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rod.Integration.Tests;

/// <summary>
/// The operator API's published contract (architecture.md Sec 15.3, ADR-006):
/// the OpenAPI document the teamserver generates must match the spec committed
/// under <c>sdk/openapi.v1.json</c> -- that file is the interface of record the
/// SDKs are cut against, so an endpoint change that moves the contract without
/// moving the committed spec fails here. Regenerate after an intentional
/// contract change with <c>ROD_REGEN_OPENAPI=1 dotnet test --filter
/// OpenApiContractTests</c>, then review the file's diff like any other code.
/// </summary>
public class OpenApiContractTests
{
    private static readonly string ContractPath = Path.Combine(FindRepoRoot(), "sdk", "openapi.v1.json");

    [Fact]
    public async Task TheDocument_DescribesTheVersionedOperatorSurface()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(document);

        // OpenAPI 3.1 is the version ADR-006 names as the contract's format.
        Assert.StartsWith("3.1.", (string?)document!["openapi"]);

        // The contract's address space is the versioned alias, nothing else.
        var servers = document["servers"]!.AsArray();
        Assert.Single(servers, s => (string?)s!["url"] == "/api/v1");

        var paths = document["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/engagements", paths);

        // The wire-facing surfaces are not part of the operator contract.
        Assert.DoesNotContain(paths, p => p.StartsWith("/implants/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p == "/dns-query" || p == "/mcp" || p == "/health" || p == "/");
    }

    [Fact]
    public async Task TheCommittedContract_MatchesTheServedDocument()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var served = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(served);

        var regenerate = Environment.GetEnvironmentVariable("ROD_REGEN_OPENAPI") == "1";
        if (!File.Exists(ContractPath))
        {
            if (!regenerate)
                Assert.Fail(
                    $"{ContractPath} is absent. Regenerate with ROD_REGEN_OPENAPI=1 and commit the file.");
            await File.WriteAllTextAsync(ContractPath, Canonicalize(served!));
            Assert.Fail($"{ContractPath} was absent and has been regenerated; review and commit it.");
        }

        var committed = JsonNode.Parse(await File.ReadAllTextAsync(ContractPath));
        Assert.NotNull(committed);

        if (regenerate)
        {
            await File.WriteAllTextAsync(ContractPath, Canonicalize(served!));
        }

        Assert.True(
            JsonNode.DeepEquals(committed, served),
            "The served OpenAPI document no longer matches sdk/openapi.v1.json. " +
            "If the contract change is intentional, regenerate with ROD_REGEN_OPENAPI=1 " +
            "dotnet test --filter OpenApiContractTests and commit the spec diff with the change.");
    }

    private static string Canonicalize(JsonNode document)
        => document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
