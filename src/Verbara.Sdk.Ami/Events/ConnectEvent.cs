using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Connect")]
public sealed class ConnectEvent : ManagerEvent
{
    public string? ProtocolIdentifier { get; set; }
}

