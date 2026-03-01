using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeListRooms")]
public sealed class ConfbridgeListRoomsEvent : ResponseEvent
{
    public string? Conference { get; set; }
    public int? Parties { get; set; }
    public int? Marked { get; set; }
    public bool? Locked { get; set; }
    public string? Muted { get; set; }
}

