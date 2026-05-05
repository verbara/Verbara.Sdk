using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("RtpReceiverStat")]
public sealed class RtpReceiverStatEvent : ManagerEvent
{
    public long? ReceivedPackets { get; set; }
    public double? Transit { get; set; }
    public long? RrCount { get; set; }
    public string? AccountCode { get; set; }
}

