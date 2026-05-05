using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ProtocolIdentifierReceived")]
public sealed class ProtocolIdentifierReceivedEvent : ManagerEvent
{
    public string? ProtocolIdentifier { get; set; }
}

