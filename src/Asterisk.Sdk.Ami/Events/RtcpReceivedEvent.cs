using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("RtcpReceived")]
public sealed class RtcpReceivedEvent : ManagerEvent
{
    public int? FromPort { get; set; }
    public int? ToPort { get; set; }
    public long? Pt { get; set; }
    public long? ReceptionReports { get; set; }
    public long? SenderSsrc { get; set; }
    public long? PacketsLost { get; set; }
    public long? HighestSequence { get; set; }
    public long? SequenceNumberCycles { get; set; }
    public double? LastSr { get; set; }
    public double? Rtt { get; set; }
    public long? RttAsMillseconds { get; set; }
    public string? Channel { get; set; }
    public string? Language { get; set; }
    public string? Report0SequenceNumberCycles { get; set; }
    public string? Ssrc { get; set; }
    public string? Report0lsr { get; set; }
    public long? SentOctets { get; set; }
    public string? Report0Sourcessrc { get; set; }
    public double? Report0dlsr { get; set; }
    public string? Uniqueid { get; set; }
    public int? Report0CumulativeLost { get; set; }
    public int? Report0FractionLost { get; set; }
    public long? Report0iaJitter { get; set; }
    public string? Sentntp { get; set; }
    public long? Sentrtp { get; set; }
    public int? ReportCount { get; set; }
    public int? Report0HighestSequence { get; set; }
    public string? LinkedId { get; set; }
    public long? SentPackets { get; set; }
    public string? AccountCode { get; set; }
    public double? Mes { get; set; }
}

