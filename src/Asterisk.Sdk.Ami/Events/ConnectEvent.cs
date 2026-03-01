using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Connect")]
public sealed class ConnectEvent : ManagerEvent
{
    public string? ProtocolIdentifier { get; set; }
}

