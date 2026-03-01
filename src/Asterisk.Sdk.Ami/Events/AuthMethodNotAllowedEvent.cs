using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AuthMethodNotAllowed")]
public sealed class AuthMethodNotAllowedEvent : SecurityEventBase
{
    public string? AuthMethod { get; set; }
}

