using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ChannelsHungupListComplete")]
public sealed class ChannelsHungupListComplete : ResponseEvent
{
    public int? ListItems { get; set; }
}

