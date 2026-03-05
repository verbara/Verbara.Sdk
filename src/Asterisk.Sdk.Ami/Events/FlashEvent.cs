using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Flash")]
public sealed class FlashEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
}
