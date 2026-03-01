using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ProtocolIdentifierReceived")]
public sealed class ProtocolIdentifierReceivedEvent : ManagerEvent
{
    public string? ProtocolIdentifier { get; set; }
}

