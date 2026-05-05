using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SoftHangupRequest")]
public sealed class SoftHangupRequestEvent : ChannelEventBase
{
    public int? Cause { get; set; }
    /// <summary>Technology-specific cause code (e.g. SIP response code). Asterisk 20.17+/22.7+/23+.</summary>
    public string? TechCause { get; set; }
    public string? LinkedId { get; set; }
}

