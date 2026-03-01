using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("HangupHandlerRun")]
public sealed class HangupHandlerRunEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
    public string? Handler { get; set; }
}

