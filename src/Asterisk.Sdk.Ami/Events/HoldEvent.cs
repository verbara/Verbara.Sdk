using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Hold")]
public sealed class HoldEvent : ChannelEventBase
{
    public string? MusicClass { get; set; }
}

