using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ParkedCallSwap")]
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
