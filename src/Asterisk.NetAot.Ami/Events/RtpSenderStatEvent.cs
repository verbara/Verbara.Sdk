using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("RtpSenderStat")]
public sealed class RtpSenderStatEvent : ManagerEvent
{
    public long? SentPackets { get; set; }
    public long? SrCount { get; set; }
    public double? Rtt { get; set; }
}

