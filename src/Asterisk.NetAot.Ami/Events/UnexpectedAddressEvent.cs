using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("UnexpectedAddress")]
public sealed class UnexpectedAddressEvent : SecurityEventBase
{
    public string? ExpectedAddress { get; set; }
}

