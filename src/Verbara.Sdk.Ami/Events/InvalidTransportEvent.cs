using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("InvalidTransport")]
public sealed class InvalidTransportEvent : SecurityEventBase
{
    public string? AttemptedTransport { get; set; }
}

