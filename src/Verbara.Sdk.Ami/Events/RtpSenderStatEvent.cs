using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("RtpSenderStat")]
public sealed class RtpSenderStatEvent : ManagerEvent
{
    public long? SentPackets { get; set; }
    public long? SrCount { get; set; }
    public double? Rtt { get; set; }
}

