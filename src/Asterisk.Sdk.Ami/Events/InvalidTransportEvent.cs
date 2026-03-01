using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("InvalidTransport")]
public sealed class InvalidTransportEvent : SecurityEventBase
{
    public string? AttemptedTransport { get; set; }
}

