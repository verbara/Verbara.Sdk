using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("RtpReceiverStat")]
public sealed class RtpReceiverStatEvent : ManagerEvent
{
    public long? ReceivedPackets { get; set; }
    public double? Transit { get; set; }
    public long? RrCount { get; set; }
    public string? AccountCode { get; set; }
}

