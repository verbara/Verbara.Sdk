using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Join")]
public sealed class JoinEvent : ManagerEvent
{
    public string? CallerId { get; set; }
    public int? Position { get; set; }
}

