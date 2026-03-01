using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("NewCallerId")]
public sealed class NewCallerIdEvent : ChannelEventBase
{
    public int? CidCallingPres { get; set; }
    public string? CidCallingPresTxt { get; set; }
    public string? LinkedId { get; set; }
}

