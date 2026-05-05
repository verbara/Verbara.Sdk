using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AuthMethodNotAllowed")]
public sealed class AuthMethodNotAllowedEvent : SecurityEventBase
{
    public string? AuthMethod { get; set; }
}

