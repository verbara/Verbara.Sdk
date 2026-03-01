using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ChannelsHungupListComplete")]
public sealed class ChannelsHungupListComplete : ResponseEvent
{
    public int? ListItems { get; set; }
}

