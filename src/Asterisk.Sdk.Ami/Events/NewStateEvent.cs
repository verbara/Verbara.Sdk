using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("NewState")]
public sealed class NewStateEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
}

