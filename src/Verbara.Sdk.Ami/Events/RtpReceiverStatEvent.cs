using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "No supported Asterisk emits RTPReceiverStat; the core emitted it in Asterisk 1.6 and no longer does in 11. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0007",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007")]
[VerbaraMapping("RtpReceiverStat")]
public sealed class RtpReceiverStatEvent : ManagerEvent
{
    public long? ReceivedPackets { get; set; }
    public double? Transit { get; set; }
    public long? RrCount { get; set; }
    public string? AccountCode { get; set; }
}

