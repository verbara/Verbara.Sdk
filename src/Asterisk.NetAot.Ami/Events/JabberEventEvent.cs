using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("JabberEvent")]
public sealed class JabberEventEvent : ManagerEvent
{
    public string? Account { get; set; }
    public string? Packet { get; set; }
}

