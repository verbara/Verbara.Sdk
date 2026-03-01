using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Connect")]
public sealed class ConnectEvent : ManagerEvent
{
    public string? ProtocolIdentifier { get; set; }
}

