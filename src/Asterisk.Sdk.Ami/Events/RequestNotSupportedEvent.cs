using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("RequestNotSupported")]
public sealed class RequestNotSupportedEvent : SecurityEventBase
{
    public string? RequestType { get; set; }
}

