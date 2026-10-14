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
/// generated password is logged exactly once -- at the moment its hash is
/// stored -- so an unconfigured server boots with a loginable account
/// instead of the lockout, and later boots stay silent.
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
        // generated password exists in clear, and it is never stored. A
        // later boot finds the hash standing and says nothing; a revoked
        // credential re-arms with a fresh password logged the same way.
        _logger.LogWarning(
            "Seeded initial admin '{Handle}' with generated password '{Password}'. "
            + "It is shown only here and never stored; pin your own with Operators:Initial:Password.",
            seed.Handle,
            password);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
