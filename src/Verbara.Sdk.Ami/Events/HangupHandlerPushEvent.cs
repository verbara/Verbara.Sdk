using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("HangupHandlerPush")]
public sealed class HangupHandlerPushEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
    public string? Handler { get; set; }
}

