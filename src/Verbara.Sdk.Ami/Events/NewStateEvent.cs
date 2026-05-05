using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("NewState")]
public sealed class NewStateEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
}

