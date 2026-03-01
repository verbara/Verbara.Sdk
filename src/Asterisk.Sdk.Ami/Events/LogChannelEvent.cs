using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("LogChannel")]
public sealed class LogChannelEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public bool? Enabled { get; set; }
    public int? Reason { get; set; }
    public string? ReasonTxt { get; set; }
}

