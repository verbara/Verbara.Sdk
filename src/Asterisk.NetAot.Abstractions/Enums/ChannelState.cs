namespace Asterisk.NetAot.Abstractions.Enums;

/// <summary>
/// Asterisk channel states (AST_STATE_*).
/// </summary>
public enum ChannelState
{
    Down = 0,
    Reserved = 1,
    OffHook = 2,
    Dialing = 3,
    Ring = 4,
    Ringing = 5,
    Up = 6,
    Busy = 7,
    DialingOffHook = 8,
    PreRing = 9,
    Unknown = 10
}
