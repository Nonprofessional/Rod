namespace Rod.Transport.Listeners;

/// <summary>
/// Where a <see cref="Listener"/> sits in its lifecycle.
/// </summary>
public enum ListenerState
{
    /// <summary>Configured but not yet bound by the host.</summary>
    Stopped,

    /// <summary>Binding is in progress.</summary>
    Starting,

    /// <summary>The socket is open and accepting implant connections.</summary>
    Running,

    /// <summary>The socket is closing.</summary>
    Stopping,
}
