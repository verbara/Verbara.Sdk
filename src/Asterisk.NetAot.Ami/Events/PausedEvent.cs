using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Paused")]
public sealed class PausedEvent : ManagerEvent
{
    public string? Header { get; set; }
    public string? Extension { get; set; }
}

