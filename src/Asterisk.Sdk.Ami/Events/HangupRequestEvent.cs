using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("HangupRequest")]
public sealed class HangupRequestEvent : ChannelEventBase
{
    public int? Cause { get; set; }
    public string? LinkedId { get; set; }
}

