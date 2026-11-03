using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ParkedCall")]
public sealed class ParkedCallEvent : ParkingEventBase
{
    /// <summary>The seconds left before the parked call times out, from the <c>ParkingTimeout</c> header.</summary>
    [VerbaraMapping("ParkingTimeout")]
    public int? Timeout { get; set; }

    public string? Parkeelinkedid { get; set; }
}
