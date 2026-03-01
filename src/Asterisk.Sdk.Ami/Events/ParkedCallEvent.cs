using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ParkedCall")]
public sealed class ParkedCallEvent : ChannelEventBase
{
    public int? Timeout { get; set; }
    public string? Parkeelinkedid { get; set; }
}

