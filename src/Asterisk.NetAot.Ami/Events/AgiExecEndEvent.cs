using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AgiExecEnd")]
public sealed class AgiExecEndEvent : ManagerEvent
{
    public string? Linkedid { get; set; }
    public string? Language { get; set; }
}

