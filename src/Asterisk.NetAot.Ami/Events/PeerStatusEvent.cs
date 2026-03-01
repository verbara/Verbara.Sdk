using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("PeerStatus")]
public sealed class PeerStatusEvent : ManagerEvent
{
    public string? ChannelType { get; set; }
    public string? Peer { get; set; }
    public string? PeerStatus { get; set; }
    public string? Cause { get; set; }
    public int? Time { get; set; }
    public string? Address { get; set; }
    public int? Port { get; set; }
}

