using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("PeerStatus")]
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

