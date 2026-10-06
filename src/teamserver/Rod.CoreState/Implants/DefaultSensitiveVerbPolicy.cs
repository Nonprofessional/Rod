using Rod.CoreState.Automation;

namespace Rod.CoreState.Implants;

/// <summary>
/// The static-fallback <see cref="ISensitiveVerbPolicy"/>: the Windows-sensitive
/// three and the evasion/exploit namespaces, read off
/// <see cref="AutomationLimits"/>. It stands alone in hosts without the
/// tradecraft layer and stays loaded as the registry-backed implementation's
/// floor -- a verb the fallback lists sensitive stays sensitive no matter what
/// any module registers.
/// </summary>
public sealed class DefaultSensitiveVerbPolicy : ISensitiveVerbPolicy
{
    public bool IsSensitive(string verb)
        => AutomationLimits.IsSensitiveVerb(verb);
}
