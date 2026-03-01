using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("RequestNotSupported")]
public sealed class RequestNotSupportedEvent : SecurityEventBase
{
    public string? RequestType { get; set; }
}

