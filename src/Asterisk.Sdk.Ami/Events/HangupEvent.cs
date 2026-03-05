using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Hangup")]
public sealed class HangupEvent : ChannelEventBase
{
    public int? Cause { get; set; }
    public string? CauseTxt { get; set; }
    /// <summary>Technology-specific cause code (e.g. SIP response code). Asterisk 20.17+/22.7+/23+.</summary>
    public string? TechCause { get; set; }
    public string? LinkedId { get; set; }
}

