using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ConfbridgeListRooms")]
public sealed class ConfbridgeListRoomsEvent : ResponseEvent
{
    public string? Conference { get; set; }
    public int? Parties { get; set; }
    public int? Marked { get; set; }
    public bool? Locked { get; set; }
    public string? Muted { get; set; }
}

