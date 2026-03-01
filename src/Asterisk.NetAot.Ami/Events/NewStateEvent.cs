using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("NewState")]
public sealed class NewStateEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
}

