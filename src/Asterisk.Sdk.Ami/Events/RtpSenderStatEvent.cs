using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("RtpSenderStat")]
public sealed class RtpSenderStatEvent : ManagerEvent
{
    public long? SentPackets { get; set; }
    public long? SrCount { get; set; }
    public double? Rtt { get; set; }
}

