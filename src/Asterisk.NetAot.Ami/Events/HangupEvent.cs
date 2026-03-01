using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Hangup")]
public sealed class HangupEvent : ChannelEventBase
{
    public int? Cause { get; set; }
    public string? CauseTxt { get; set; }
    public string? LinkedId { get; set; }
}

