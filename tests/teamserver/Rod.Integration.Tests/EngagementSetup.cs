using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// The engagement ladder most suites climb: create the engagement, mint its
/// deploy token, enroll an implant -- over the operator HTTP API, except the
/// repository-direct enroll for suites that need the entity without the
/// enroll wire shape. One definition so the setup reads the same everywhere
/// and a route change lands in one file; the name defaults to a unique value
/// because most suites never read it back.
/// </summary>
internal static class EngagementSetup
{
    public static async Task<string> CreateEngagementAsync(HttpClient client, string? name = null)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: name ?? $"Operation {Guid.NewGuid():N}"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    public static async Task<string> MintDeployTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/deploy-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.DeployTokenResponse>();
        return token!.Secret;
    }

    /// <summary>
    /// Enrolls over the wire without a public key -- the dev shape where the
    /// server accepts a keyless enrollment for suites that only need an
    /// implant row.
    /// </summary>
    public static async Task<string> EnrollAsync(HttpClient client, string secret)
    {
        var response = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(DeployTokenSecret: secret, Class: null, PublicKey: null));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        return enrolled!.ImplantId!;
    }

    /// <summary>Enrolls over the wire with a fresh ECDSA P-256 key, the Tier 0 shape.</summary>
    public static async Task<string> EnrollImplantAsync(HttpClient http, string secret)
    {
        var leafKey = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var spki = leafKey.ExportSubjectPublicKeyInfo();

        var response = await http.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(
                DeployTokenSecret: secret, Class: null, PublicKey: Convert.ToBase64String(spki)));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();

        return enrolled!.ImplantId!;
    }

    /// <summary>Enrolls directly through the repository, no wire round trip.</summary>
    public static async Task<Implant> EnrollImplantAsync(IHost host, EngagementId engagement)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(ImplantId.New(), engagement, now.AddDays(30), ImplantClass.Implant, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    /// <summary>
    /// Creates an engagement directly through the service, owned by the seeded
    /// operator (the login the auth-cookie suites use) -- the repository-direct
    /// twin of the HTTP create, for suites that drive the operator API against
    /// an implant they enrolled themselves.
    /// </summary>
    public static async Task<EngagementId> CreateOwnedEngagementAsync(IHost host)
    {
        var engagements = host.Services.GetRequiredService<Rod.CoreState.Application.EngagementService>();
        var operators = host.Services.GetRequiredService<IOperatorRepository>();
        var seeded = await operators.FindByHandleAsync(AuthenticatedHost.Handle)
            ?? throw new InvalidOperationException("The seeded operator was not found.");
        var created = await engagements.CreateEngagementAsync(
            new Rod.CoreState.Application.CreateEngagementCommand(
                seeded.Id, $"Operation {Guid.NewGuid():N}"));
        return created.EngagementId;
    }

    /// <summary>
    /// Creates an engagement directly through the service -- owned by the seeded
    /// operator, the login the auth-cookie suites use -- and enrolls an implant
    /// into it without any wire round trip. The repository-direct twin of the
    /// HTTP ladder, for suites that task over the socket but never drive the
    /// operator API's create route: the engagement must exist and be reachable
    /// by the logged-in operator, because every engagement-scoped route now
    /// resolves the caller's standing (architecture.md Sec 3).
    /// </summary>
    public static async Task<Implant> EnrollOwnedImplantAsync(
        IHost host, ImplantClass @class = ImplantClass.Implant)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var engagementId = await CreateOwnedEngagementAsync(host);

        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(ImplantId.New(), engagementId, now.AddDays(30), @class, now);
        await implants.SaveAsync(implant);
        return implant;
    }
}
