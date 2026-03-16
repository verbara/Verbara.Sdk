namespace Asterisk.Sdk.Enums;

/// <summary>Connection state of an ARI client.</summary>
public enum AriConnectionState
{
    /// <summary>Client created, not yet connected.</summary>
    Initial,
    /// <summary>Connection attempt in progress.</summary>
    Connecting,
    /// <summary>WebSocket connected and receiving events.</summary>
    Connected,
    /// <summary>Connection lost, reconnect loop active.</summary>
    Reconnecting,
    /// <summary>Disconnect requested, cleanup in progress.</summary>
    Disconnecting,
    /// <summary>Cleanly disconnected.</summary>
    Disconnected,
    /// <summary>Unrecoverable error (auth failure, DNS, max retries).</summary>
    Faulted
}
