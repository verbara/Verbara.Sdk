using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueParams")]
public sealed class QueueParamsEvent : ResponseEvent
{
    public string? Queue { get; set; }
    public int? Max { get; set; }
    public string? Strategy { get; set; }
    public int? Calls { get; set; }
    public int? HoldTime { get; set; }
    public int? TalkTime { get; set; }
    public int? Completed { get; set; }
    public int? Abandoned { get; set; }
    public int? ServiceLevel { get; set; }
    public double? ServiceLevelPerf { get; set; }
    public int? Weight { get; set; }
    public double? ServiceLevelPerf2 { get; set; }
}

