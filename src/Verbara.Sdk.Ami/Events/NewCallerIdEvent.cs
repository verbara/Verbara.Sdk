using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("NewCallerId")]
public sealed class NewCallerIdEvent : ChannelEventBase
{
    public int? CidCallingPres { get; set; }
    public string? CidCallingPresTxt { get; set; }
    public string? LinkedId { get; set; }
}

