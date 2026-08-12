using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rod.CoreState;
using Rod.CoreState.Operators;

namespace Rod.Operators.Auth;

/// <summary>
/// Provisions the initial operator at startup so someone can log in before any
/// management path exists. Reads the configured <c>Operators:Initial</c> account
/// and, when no operator owns that handle yet, registers one and stores the hash
/// of its password. Idempotent: a handle that already exists is left alone, and
/// an existing operator missing a password is re-provisioned with one. In
/// Development a built-in dev account is used when configuration supplies none,
/// the same dev-default stance the implant CA takes; in Production no fallback is
/// assumed -- a server configured without an initial operator simply starts with
/// no loginable account, and operators must be provisioned by configuration.
/// </summary>
public sealed class OperatorAuthBootstrap : IHostedService
{
    private const string DevHandle = "operator";
    private const string DevDisplayName = "Development Operator";
    private const string DevPassword = "operator";

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
        if (seed is null)
        {
            _logger.LogWarning(
                "No initial operator configured (Operators:Initial) and no Development fallback applies; "
                + "no operator can log in until one is provisioned by configuration.");
            return;
        }

        var existing = await _operators.FindByHandleAsync(seed.Handle, cancellationToken);
        if (existing is null)
        {
            existing = Operator.Register(OperatorId.New(), seed.Handle, seed.DisplayName, _time.GetUtcNow());
            await _operators.SaveAsync(existing, cancellationToken);
        }

        var hash = await _credentials.FindHashAsync(existing.Id, cancellationToken);
        if (hash is not null)
            return; // Operator already has a password; leave the account untouched.

        var newHash = _hasher.HashPassword(existing, seed.Password);
        await _credentials.SetHashAsync(existing.Id, newHash, cancellationToken);
        _logger.LogInformation("Seeded initial operator '{Handle}'.", seed.Handle);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private InitialOperatorOptions? ResolveSeed()
    {
        var initial = _options.Value.Initial;
        if (initial is not null
            && !string.IsNullOrWhiteSpace(initial.Handle)
            && !string.IsNullOrWhiteSpace(initial.Password))
        {
            // A configured account wins. Fall back to the configured display name
            // or the handle when it is blank, so a minimal config (handle +
            // password) still seeds a valid operator.
            return new InitialOperatorOptions
            {
                Handle = initial.Handle,
                DisplayName = string.IsNullOrWhiteSpace(initial.DisplayName) ? initial.Handle : initial.DisplayName,
                Password = initial.Password,
            };
        }

        if (_environment.IsDevelopment())
        {
            _logger.LogWarning(
                "No initial operator configured; seeding the built-in Development account '{Handle}' "
                + "with password '{Password}'. This MUST NOT be used in Production.",
                DevHandle,
                DevPassword);
            return new InitialOperatorOptions
            {
                Handle = DevHandle,
                DisplayName = DevDisplayName,
                Password = DevPassword,
            };
        }

        return null;
    }
}
