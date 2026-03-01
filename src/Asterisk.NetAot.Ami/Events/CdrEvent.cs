using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Cdr")]
public sealed class CdrEvent : ManagerEvent
{
    public string? AccountCode { get; set; }
    public string? Src { get; set; }
    public string? Destination { get; set; }
    public string? DestinationContext { get; set; }
    public string? CallerId { get; set; }
    public string? Channel { get; set; }
    public string? DestinationChannel { get; set; }
    public string? LastApplication { get; set; }
    public string? LastData { get; set; }
    public string? StartTime { get; set; }
    public DateTimeOffset? StartTimeAsDate { get; set; }
    public string? AnswerTime { get; set; }
    public DateTimeOffset? AnswerTimeAsDate { get; set; }
    public string? EndTime { get; set; }
    public DateTimeOffset? EndTimeAsDate { get; set; }
    public int? Duration { get; set; }
    public int? BillableSeconds { get; set; }
    public string? Disposition { get; set; }
    public string? AmaFlags { get; set; }
    public string? UserField { get; set; }
    public string? Recordfile { get; set; }
}

