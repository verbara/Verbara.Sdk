using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("UnexpectedAddress")]
public sealed class UnexpectedAddressEvent : SecurityEventBase
{
    public string? ExpectedAddress { get; set; }
}

