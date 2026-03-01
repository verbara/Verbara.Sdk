using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AgiExecEnd")]
public sealed class AgiExecEndEvent : ManagerEvent
{
    public string? Linkedid { get; set; }
    public string? Language { get; set; }
}

