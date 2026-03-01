using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ChannelUpdate")]
public sealed class ChannelUpdateEvent : ManagerEvent
{
    public string? ChannelType { get; set; }
    public string? Channel { get; set; }
    public string? SipCallId { get; set; }
    public string? SipFullContact { get; set; }
    public string? PeerName { get; set; }
    public string? GtalkSid { get; set; }
    public string? Iax2CallNoLocal { get; set; }
    public string? Iax2CallNoRemote { get; set; }
    public string? Iax2Peer { get; set; }
}

