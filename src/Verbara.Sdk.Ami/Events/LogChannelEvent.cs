using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("LogChannel")]
public sealed class LogChannelEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public bool? Enabled { get; set; }
    public int? Reason { get; set; }
    public string? ReasonTxt { get; set; }
}

