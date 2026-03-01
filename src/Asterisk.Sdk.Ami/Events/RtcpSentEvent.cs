using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("RtcpSent")]
public sealed class RtcpSentEvent : ManagerEvent
{
    public int? FromPort { get; set; }
    public long? Pt { get; set; }
    public int? ToPort { get; set; }
    public long? OurSsrc { get; set; }
    public double? SentNtp { get; set; }
    public long? SentRtp { get; set; }
    public long? SentPackets { get; set; }
    public long? SentOctets { get; set; }
    public long? CumulativeLoss { get; set; }
    public long? TheirLastSr { get; set; }
    public string? Channel { get; set; }
    public string? Language { get; set; }
    public string? Report0SequenceNumberCycles { get; set; }
    public string? Ssrc { get; set; }
    public string? LinkedId { get; set; }
    public string? Report0lsr { get; set; }
    public string? Report0Sourcessrc { get; set; }
    public double? Report0dlsr { get; set; }
    public string? Uniqueid { get; set; }
    public int? ReportCount { get; set; }
    public int? Report0CumulativeLost { get; set; }
    public int? Report0FractionLost { get; set; }
    public int? Report0iaJitter { get; set; }
    public int? Report0HighestSequence { get; set; }
    public string? AccountCode { get; set; }
    public double? Mes { get; set; }
}

