using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("HangupHandlerPush")]
public sealed class HangupHandlerPushEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
    public string? Handler { get; set; }
}

