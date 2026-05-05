using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ParkedCallSwap")]
public sealed class ParkedCallSwapEvent : ChannelEventBase
{
    public string? ParkeeChannel { get; set; }
    public string? ParkeeChannelState { get; set; }
    public string? ParkerChannel { get; set; }
    public string? ParkingSpace { get; set; }
    public string? ParkingLot { get; set; }
    public string? ParkingTimeout { get; set; }
    public string? LinkedId { get; set; }
}
