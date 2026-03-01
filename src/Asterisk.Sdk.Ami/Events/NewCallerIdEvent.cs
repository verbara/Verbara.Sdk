using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("NewCallerId")]
public sealed class NewCallerIdEvent : ChannelEventBase
{
    public int? CidCallingPres { get; set; }
    public string? CidCallingPresTxt { get; set; }
    public string? LinkedId { get; set; }
}

