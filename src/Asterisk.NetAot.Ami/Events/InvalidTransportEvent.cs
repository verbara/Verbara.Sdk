using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("InvalidTransport")]
public sealed class InvalidTransportEvent : SecurityEventBase
{
    public string? AttemptedTransport { get; set; }
}

