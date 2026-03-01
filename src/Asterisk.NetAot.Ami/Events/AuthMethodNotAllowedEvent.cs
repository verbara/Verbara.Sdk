using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AuthMethodNotAllowed")]
public sealed class AuthMethodNotAllowedEvent : SecurityEventBase
{
    public string? AuthMethod { get; set; }
}

