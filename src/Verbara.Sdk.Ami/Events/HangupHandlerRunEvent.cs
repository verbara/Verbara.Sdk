using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("HangupHandlerRun")]
public sealed class HangupHandlerRunEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
    public string? Handler { get; set; }
}

