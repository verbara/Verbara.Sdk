using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

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

