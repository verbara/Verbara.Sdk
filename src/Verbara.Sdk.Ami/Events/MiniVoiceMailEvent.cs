using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MiniVoiceMail")]
public sealed class MiniVoiceMailEvent : ChannelEventBase
{
    public string? Mailbox { get; set; }
    public string? Counter { get; set; }
    public string? LinkedId { get; set; }
}
