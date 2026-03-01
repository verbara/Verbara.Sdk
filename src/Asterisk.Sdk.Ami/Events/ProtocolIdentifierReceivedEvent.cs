using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ProtocolIdentifierReceived")]
public sealed class ProtocolIdentifierReceivedEvent : ManagerEvent
{
    public string? ProtocolIdentifier { get; set; }
}

