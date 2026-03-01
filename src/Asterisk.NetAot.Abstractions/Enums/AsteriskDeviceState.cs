namespace Asterisk.NetAot.Abstractions.Enums;

/// <summary>
/// Asterisk device states (AST_DEVICE_*).
/// </summary>
public enum AsteriskDeviceState
{
    Unknown = 0,
    NotInUse = 1,
    InUse = 2,
    Busy = 3,
    Invalid = 4,
    Unavailable = 5,
    Ringing = 6,
    RingInUse = 7,
    OnHold = 8
}
