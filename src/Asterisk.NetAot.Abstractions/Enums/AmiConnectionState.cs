namespace Asterisk.NetAot.Abstractions.Enums;

/// <summary>
/// States of an AMI connection lifecycle.
/// </summary>
public enum AmiConnectionState
{
    Initial,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Disconnected
}
