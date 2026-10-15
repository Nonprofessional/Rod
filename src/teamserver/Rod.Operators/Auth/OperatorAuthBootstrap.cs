using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rod.CoreState;
using Rod.CoreState.Operators;

namespace Rod.Operators.Auth;

/// <summary>
/// Provisions the initial admin at startup so someone can log in before any
/// management path exists. Reads the configured <c>Operators:Initial</c>
/// account and, when no operator owns that handle yet, registers one and
/// stores the hash of its password. The seed is always a deployment admin --
/// the deployment's root of trust, since the console only grows members.
/// Idempotent: a handle that already exists is left alone, and an existing
/// operator missing a password is re-provisioned with one.
/// The handle defaults to <c>admin</c>, and the password to <c>admin</c> in
/// Development. Outside Development an unset password is generated, and the
/// generated password is revealed exactly once -- at the moment its hash is
/// stored -- into a read-owner-only file beside the server binary, never
/// into the log stream (journals and log shippers outlive the moment, and
/// this is the deployment's root-of-trust credential). The log names the
/// file's path and nothing else; later boots find the hash standing and
/// write nothing.
/// </summary>
public sealed class OperatorAuthBootstrap : IHostedService
{
    private const string DefaultHandle = "admin";
    private const string DevPassword = "admin";
    private const int GeneratedPasswordLength = 32;

    private readonly IOptions<OperatorAuthOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly IOperatorRepository _operators;
    private readonly IOperatorCredentialStore _credentials;
    private readonly IPasswordHasher<Operator> _hasher;
    private readonly TimeProvider _time;
    private readonly ILogger<OperatorAuthBootstrap> _logger;

    public OperatorAuthBootstrap(
        IOptions<OperatorAuthOptions> options,
        IHostEnvironment environment,
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IPasswordHasher<Operator> hasher,
        TimeProvider time,
        ILogger<OperatorAuthBootstrap> logger)
    {
        _options = options;
        _environment = environment;
        _operators = operators;
        _credentials = credentials;
        _hasher = hasher;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var seed = ResolveSeed();

        var existing = await _operators.FindByHandleAsync(seed.Handle, cancellationToken);
        if (existing is null)
        {
            // The display name is derived from the handle: the seed is
            // deployment plumbing, and a deployment sets a name and a
            // password, nothing else.
            existing = Operator.Register(
                OperatorId.New(), seed.Handle, seed.Handle, _time.GetUtcNow(), DeploymentRole.Admin);
            await _operators.SaveAsync(existing, cancellationToken);
        }

        var hash = await _credentials.FindHashAsync(existing.Id, cancellationToken);
        if (hash is not null)
            return; // Operator already has a password; leave the account untouched.

        var password = seed.ConfiguredPassword ?? GeneratePassword();
        var newHash = _hasher.HashPassword(existing, password);
        await _credentials.SetHashAsync(existing.Id, newHash, cancellationToken);

        if (seed.ConfiguredPassword is not null)
        {
            _logger.LogInformation("Seeded initial admin '{Handle}'.", seed.Handle);
            return;
        }

        // The one-time reveal: this is the only place and moment the
        // generated password exists in clear, and it is never stored. It
        // lands in a read-owner-only file beside the server binary, not in
        // the log stream -- the deployment's root-of-trust credential must
        // not survive in journals and log shippers. A later boot finds the
        // hash standing and writes nothing; a revoked credential re-arms
        // with a fresh password revealed the same way.
        var revealPath = WritePasswordReveal(seed.Handle, password);
        _logger.LogWarning(
            "Seeded initial admin '{Handle}' with a generated password; it is written to {RevealPath} "
            + "(read it, delete the file, then pin your own with Operators:Initial:Password).",
            seed.Handle,
            revealPath);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static string WritePasswordReveal(string handle, string password)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"initial-{handle}-password.txt");
        File.WriteAllText(path, password + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    /// <summary>
    /// Resolves the seed account from configuration, one field at a time: a
    /// configured field wins, the handle falls back to <c>admin</c>, and the
    /// password falls back to the Development default or, outside
    /// Development, to a generated password (a <c>null</c> configured
    /// password here means "generate at the moment the hash is stored").
    /// </summary>
    private Seed ResolveSeed()
    {
        var initial = _options.Value.Initial;
        var handle = string.IsNullOrWhiteSpace(initial?.Handle)
            ? DefaultHandle
            : initial!.Handle.Trim();

        var password = string.IsNullOrWhiteSpace(initial?.Password) ? null : initial!.Password;

        if (password is null && _environment.IsDevelopment())
        {
            _logger.LogWarning(
                "No initial operator configured (Operators:Initial); seeding the built-in Development "
                + "account '{Handle}' with password '{Password}'. This MUST NOT be used in Production.",
                DefaultHandle,
                DevPassword);
            password = DevPassword;
        }

        return new Seed(handle, password);
    }

    private static string GeneratePassword()
        => RandomNumberGenerator.GetHexString(GeneratedPasswordLength);

    private sealed record Seed(string Handle, string? ConfiguredPassword);
}
