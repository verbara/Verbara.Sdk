namespace Asterisk.Sdk.Enums;

/// <summary>Lifecycle state of an AGI server.</summary>
public enum AgiServerState
{
    /// <summary>Server not running.</summary>
    Stopped,
    /// <summary>Server starting, binding port.</summary>
    Starting,
    /// <summary>Actively listening for AGI connections.</summary>
    Listening,
    /// <summary>Shutdown in progress.</summary>
    Stopping,
    /// <summary>Listener faulted (address in use, fd exhaustion).</summary>
    Faulted
}
