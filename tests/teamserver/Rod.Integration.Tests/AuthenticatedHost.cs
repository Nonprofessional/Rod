using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.Operators;
using Rod.Operators.Auth;
using Rod.Operators.Endpoints;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// Shared host factory for operator-authenticated integration tests. Composes the
/// transport core with the operator and operator-auth layers -- the same shape the
/// teamserver host assembles -- and seeds the initial operator from configuration
/// so a test can establish the cookie session that every operator-facing endpoint
/// now requires (operator authentication). The server is
/// the sole authority on who the caller is: the seeded operator's id is read back
/// from the repository, and the audit trail records that server-resolved identity
/// rather than any client-supplied value.
///
/// Two entry points cover the two host shapes the suite uses. <see cref="Create"/>
/// builds the in-memory <c>TestServer</c> host and returns a cookie-persisting
/// client; the real-Kestrel tests (which bind sockets for gRPC/mTLS) call
/// <see cref="ComposeServices"/>/<see cref="ComposeEndpoints"/> inside their own
/// <see cref="TransportHost.CreateHostBuilder"/> call and wrap their client with
/// <see cref="CookieHandler"/> themselves.
/// </summary>
internal static class AuthenticatedHost
{
    public const string Handle = "operator";
    public const string DisplayName = "Test Operator";
    public const string Password = "p@ssw0rd!";

    /// <summary>
    /// Builds the in-memory configuration carrying the seed operator, extended with
    /// any additional settings the caller needs (for example
    /// <c>Audit:DataDirectory</c> to opt into the durable stores, or listener
    /// bindings). The seed is always present so a login is always available.
    /// </summary>
    public static IConfiguration BuildConfig(
        Action<Dictionary<string, string?>>? extend = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Operators:Initial:Handle"] = Handle,
            ["Operators:Initial:DisplayName"] = DisplayName,
            ["Operators:Initial:Password"] = Password,
        };
        extend?.Invoke(settings);
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    /// Composes the operator and operator-auth services onto the transport service
    /// collection. Intended as the body of the <c>configureServices</c> hook passed
    /// to <see cref="TransportHost.CreateHostBuilder"/>; an optional hook lets a
    /// caller layer further services (tradecraft capabilities, durable stores).
    /// </summary>
    public static void ComposeServices(
        IServiceCollection services,
        IConfiguration configuration,
        Action<IServiceCollection>? extra = null)
    {
        services.AddRodOperators();
        services.AddRodOperatorAuth(configuration);
        extra?.Invoke(services);
    }

    /// <summary>
    /// Maps the operator SSE stream and the login/logout/me routes. Intended as the
    /// body of the <c>mapEndpoints</c> hook passed to
    /// <see cref="TransportHost.CreateHostBuilder"/> (the transport core already
    /// maps every other operator-facing endpoint via
    /// <see cref="TransportHost.MapRodEndpoints"/>); an optional hook lets a caller
    /// map further endpoints (tradecraft capabilities).
    /// </summary>
    public static void ComposeEndpoints(
        IEndpointRouteBuilder endpoints,
        Action<IEndpointRouteBuilder>? extra = null)
    {
        endpoints.MapOperatorEndpoints();
        endpoints.MapOperatorAuthEndpoints();
        extra?.Invoke(endpoints);
    }

    /// <summary>
    /// Builds and starts the in-memory <see cref="TestServer"/> with the operator
    /// and operator-auth layers composed onto the transport core.
    /// </summary>
    /// <returns>
    /// A cookie-persisting client, the running host (dispose when done), and the
    /// seeded operator's id. The client is <em>not</em> logged in; call
    /// <see cref="LoginAsync"/> to establish the session before hitting an
    /// operator-facing endpoint, or omit it to assert the 401 an anonymous request
    /// now receives.
    /// </returns>
    public static (HttpClient Client, IHost Host, OperatorId OperatorId) Create(
        Action<IServiceCollection>? configureServices = null,
        Action<IEndpointRouteBuilder>? mapEndpoints = null,
        Action<Dictionary<string, string?>>? extendConfig = null)
    {
        var config = BuildConfig(extendConfig);

        IHost host = TransportHost.CreateHostBuilder(
                configureServices: services => ComposeServices(services, config, configureServices),
                mapEndpoints: endpoints => ComposeEndpoints(endpoints, mapEndpoints),
                configuration: config)
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
            .Build();
        host.Start();

        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        var client = new HttpClient(new CookieHandler(server.CreateHandler()))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        return (client, host, GetOperatorId(host));
    }

    /// <summary>Resolves the seeded operator's id from a running host.</summary>
    public static OperatorId GetOperatorId(IHost host)
    {
        var operators = host.Services.GetRequiredService<IOperatorRepository>();
        var seeded = operators.FindByHandleAsync(Handle).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The seeded operator was not found.");
        return seeded.Id;
    }

    /// <summary>Establishes the operator cookie session. Asserts the login succeeded.</summary>
    public static async Task LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/operators/login", new LoginRequest(Handle, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Registers an additional operator on a running host -- the same provisioning
    /// steps the bootstrap seed performs, available to tests that need a second
    /// account (for example the two-operator live SSE scenario). Creates the
    /// aggregate, stores the password hash, and returns the new operator's id.
    /// </summary>
    public static async Task<OperatorId> RegisterOperatorAsync(
        IHost host, string handle, string displayName, string password)
    {
        var operators = host.Services.GetRequiredService<IOperatorRepository>();
        var credentials = host.Services.GetRequiredService<IOperatorCredentialStore>();
        var hasher = host.Services.GetRequiredService<IPasswordHasher<Operator>>();

        var op = Operator.Register(OperatorId.New(), handle, displayName, DateTimeOffset.UtcNow);
        await operators.SaveAsync(op);
        var hash = hasher.HashPassword(op, password);
        await credentials.SetHashAsync(op.Id, hash);
        return op.Id;
    }

    /// <summary>
    /// Establishes a cookie session for a specific operator -- the login twin of
    /// <see cref="RegisterOperatorAsync"/>. Use a separate cookie-persisting
    /// client per operator so the sessions stay distinct. Asserts success.
    /// </summary>
    public static async Task LoginAsync(HttpClient client, string handle, string password)
    {
        var response = await client.PostAsJsonAsync("/operators/login", new LoginRequest(handle, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Creates a fresh cookie-persisting client against the in-memory
    /// <see cref="TestServer"/>, not yet logged in. A test that needs more than
    /// one operator session (the live SSE scenario) wraps one client per operator
    /// and logs each in separately.
    /// </summary>
    public static HttpClient CreateClient(IHost host)
    {
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        return new HttpClient(new CookieHandler(server.CreateHandler()))
        {
            BaseAddress = new Uri("http://localhost"),
        };
    }
}
