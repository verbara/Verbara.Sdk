using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("UnexpectedAddress")]
public sealed class UnexpectedAddressEvent : SecurityEventBase
{
    public string? ExpectedAddress { get; set; }
}

