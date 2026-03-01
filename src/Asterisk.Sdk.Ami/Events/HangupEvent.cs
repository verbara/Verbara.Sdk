using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Hangup")]
public sealed class HangupEvent : ChannelEventBase
{
    public int? Cause { get; set; }
    public string? CauseTxt { get; set; }
    public string? LinkedId { get; set; }
}

