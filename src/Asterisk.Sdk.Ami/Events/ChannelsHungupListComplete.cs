using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ChannelsHungupListComplete")]
public sealed class ChannelsHungupListComplete : ResponseEvent
{
    public int? ListItems { get; set; }
}

