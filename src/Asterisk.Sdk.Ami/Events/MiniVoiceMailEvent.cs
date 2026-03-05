using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MiniVoiceMail")]
public sealed class MiniVoiceMailEvent : ChannelEventBase
{
    public string? Mailbox { get; set; }
    public string? Counter { get; set; }
    public string? LinkedId { get; set; }
}
