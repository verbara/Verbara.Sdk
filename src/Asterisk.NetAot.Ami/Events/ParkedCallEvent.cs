using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ParkedCall")]
public sealed class ParkedCallEvent : ChannelEventBase
{
    public int? Timeout { get; set; }
    public string? Parkeelinkedid { get; set; }
}

