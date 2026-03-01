using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AgiExecStart")]
public sealed class AgiExecStartEvent : ManagerEvent
{
    public string? Linkedid { get; set; }
    public string? Language { get; set; }
}

