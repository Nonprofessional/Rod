namespace Rod.Transport.Listeners;

/// <summary>
/// Where a <see cref="Listener"/> sits in its lifecycle. Listeners are bound at
/// host startup and repointed in place (architecture.md Sec 8), so the registry
/// only ever observes the terminal states: the host marks a listener Running
/// once its endpoint is bound, and a listener is removed rather than stopped.
/// </summary>
public enum ListenerState
{
    /// <summary>Configured but not yet bound by the host.</summary>
    Stopped,

    /// <summary>The socket is open and accepting implant connections.</summary>
    Running,
}
