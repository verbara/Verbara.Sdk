using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("RequestNotSupported")]
public sealed class RequestNotSupportedEvent : SecurityEventBase
{
    public string? RequestType { get; set; }
}

