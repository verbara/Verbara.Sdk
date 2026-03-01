using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("LogChannel")]
public sealed class LogChannelEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public bool? Enabled { get; set; }
    public int? Reason { get; set; }
    public string? ReasonTxt { get; set; }
}

