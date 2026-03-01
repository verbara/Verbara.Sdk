using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("RtpReceiverStat")]
public sealed class RtpReceiverStatEvent : ManagerEvent
{
    public long? ReceivedPackets { get; set; }
    public double? Transit { get; set; }
    public long? RrCount { get; set; }
    public string? AccountCode { get; set; }
}

