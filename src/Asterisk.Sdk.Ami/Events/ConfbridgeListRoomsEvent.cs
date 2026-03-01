using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ConfbridgeListRooms")]
public sealed class ConfbridgeListRoomsEvent : ResponseEvent
{
    public string? Conference { get; set; }
    public int? Parties { get; set; }
    public int? Marked { get; set; }
    public bool? Locked { get; set; }
    public string? Muted { get; set; }
}

