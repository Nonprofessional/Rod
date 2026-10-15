
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rod.CoreState.Operators;
using Rod.Operators.Auth;

namespace Rod.Integration.Tests;

/// <summary>
/// The operator bootstrap (architecture.md Sec 4.5 and Sec 9): the initial
/// admin the teamserver boots with. The seed is always an admin (the console
/// only grows members) and its display name derives from its handle. An
/// unset password resolves per environment -- <c>admin</c>/<c>admin</c> in
/// Development, a generated password revealed exactly once in Production
/// into a read-owner-only file beside the server binary, never the log
/// stream -- so an unconfigured server boots with a loginable account
/// instead of the lockout, and later boots stay silent.
/// </summary>
public partial class OperatorBootstrapTests
{
    [Fact]
    public async Task Production_UnsetPassword_GeneratesOnce_AndLaterBootsLogNothing()
    {
        var harness = new Harness();
        var bootstrap = harness.Build(Environments.Production);

        await bootstrap.StartAsync();

        // The default handle, the admin role, and the derived display name.
        var seeded = await harness.Operators.FindByHandleAsync("admin");
        Assert.NotNull(seeded);
        Assert.Equal(DeploymentRole.Admin, seeded!.DeploymentRole);
        Assert.Equal("admin", seeded.DisplayName);

        // The generated password was revealed exactly once -- into the
        // reveal file, never a log line -- and the hash it produced
        // verifies against that password.
        var hash = await harness.Credentials.FindHashAsync(seeded.Id);
        Assert.NotNull(hash);
        var reveal = Assert.Single(
            harness.Logger.Entries, e => e.Message.Contains("generated password"));
        Assert.Equal(LogLevel.Warning, reveal.Level);
        Assert.Contains("initial-admin-password.txt", reveal.Message);
        var password = ReadRevealFile("admin");
        Assert.False(string.IsNullOrEmpty(password));
        Assert.Equal(
            PasswordVerificationResult.Success,
            harness.Hasher.VerifyHashedPassword(seeded, hash!, password));

        // The log stream carries no password material anywhere.
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains(password));

        // The second boot over the same stores is silent and keeps the hash:
        // the account already has a password, so nothing is re-seeded.
        await bootstrap.StartAsync();
        Assert.Equal(1, harness.Logger.Entries.Count(e => e.Message.Contains("generated password")));
        Assert.Equal(hash, await harness.Credentials.FindHashAsync(seeded.Id));
    }

    [Fact]
    public async Task Production_RevokedCredential_ReArmsWithAFreshGeneratedPassword()
    {
        var harness = new Harness();
        var bootstrap = harness.Build(Environments.Production);

        await bootstrap.StartAsync();
        var seeded = await harness.Operators.FindByHandleAsync("admin");
        var first = ReadRevealFile("admin");

        // A revoked credential is a first-boot moment again: the re-arm
        // generates a new password and reveals it the same one-time way.
        await harness.Credentials.RevokeAsync(seeded!.Id);
        await bootstrap.StartAsync();

        var second = ReadRevealFile("admin");
        Assert.Equal(2, harness.Logger.Entries.Count(e => e.Message.Contains("generated password")));
        Assert.NotEqual(first, second);
        var hash = await harness.Credentials.FindHashAsync(seeded.Id);
        Assert.Equal(
            PasswordVerificationResult.Success,
            harness.Hasher.VerifyHashedPassword(seeded, hash!, second));
    }

    [Fact]
    public async Task Production_ConfiguredAccount_SeedsSilently()
    {
        var harness = new Harness();
        var bootstrap = harness.Build(
            Environments.Production,
            new InitialOperatorOptions { Handle = "root", Password = "configured-p@ss" });

        await bootstrap.StartAsync();

        // A fully configured account seeds as stated: admin role, handle as
        // display name, the configured password verifying.
        var seeded = await harness.Operators.FindByHandleAsync("root");
        Assert.NotNull(seeded);
        Assert.Equal(DeploymentRole.Admin, seeded!.DeploymentRole);
        Assert.Equal("root", seeded.DisplayName);
        var hash = await harness.Credentials.FindHashAsync(seeded.Id);
        Assert.Equal(
            PasswordVerificationResult.Success,
            harness.Hasher.VerifyHashedPassword(seeded, hash!, "configured-p@ss"));

        // And nothing sensitive is logged: no generated password, no
        // configured password, anywhere in the entries.
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("generated password"));
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("configured-p@ss"));
    }

    [Fact]
    public async Task Development_Unset_SeedsTheBuiltInAdminAdmin()
    {
        var harness = new Harness();

        await harness.Build(Environments.Development).StartAsync();

        // The Development fallback keeps dotnet run working out of the box,
        // with the loud warning it always carried.
        var seeded = await harness.Operators.FindByHandleAsync("admin");
        Assert.NotNull(seeded);
        Assert.Equal(DeploymentRole.Admin, seeded!.DeploymentRole);
        var hash = await harness.Credentials.FindHashAsync(seeded.Id);
        Assert.Equal(
            PasswordVerificationResult.Success,
            harness.Hasher.VerifyHashedPassword(seeded, hash!, "admin"));
        Assert.Contains(harness.Logger.Entries, e => e.Message.Contains("MUST NOT be used in Production"));
        // The dev default announces itself once; it is not a generated
        // reveal.
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("generated password"));
    }

    [Fact]
    public async Task FieldsResolveIndependently_HandleDefaultsToAdmin()
    {
        var harness = new Harness();

        // Only a password configured: the handle falls back to admin while
        // the configured password is honored -- the two fields no longer
        // stand or fall together.
        await harness.Build(
                Environments.Production,
                new InitialOperatorOptions { Password = "half-configured-p@ss" })
            .StartAsync();

        var seeded = await harness.Operators.FindByHandleAsync("admin");
        Assert.NotNull(seeded);
        var hash = await harness.Credentials.FindHashAsync(seeded!.Id);
        Assert.Equal(
            PasswordVerificationResult.Success,
            harness.Hasher.VerifyHashedPassword(seeded, hash!, "half-configured-p@ss"));
    }

    // The reveal file lands beside the test assembly (AppContext.BaseDirectory
    // of the harness) exactly where the server binary writes it; read whole
    // and trimmed, the way an operator reads it.
    private static string ReadRevealFile(string handle)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"initial-{handle}-password.txt")).Trim();

    private sealed class Harness
    {
        public InMemoryOperatorRepository Operators { get; } = new();
        public InMemoryOperatorCredentialStore Credentials { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public IPasswordHasher<Operator> Hasher { get; } = new PasswordHasher<Operator>();

        public OperatorAuthBootstrap Build(
            string environment, InitialOperatorOptions? initial = null)
            => new(
                Options.Create(new OperatorAuthOptions { Initial = initial }),
                new TestEnvironment(environment),
                Operators,
                Credentials,
                Hasher,
                TimeProvider.System,
                Logger);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Rod.Tests";
        public string EnvironmentName { get; set; } = name;
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger : ILogger<OperatorAuthBootstrap>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
