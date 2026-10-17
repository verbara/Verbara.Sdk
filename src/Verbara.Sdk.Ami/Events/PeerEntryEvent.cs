using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

/// <summary>
/// Response event of IAXpeerlist/IAXpeers (chan_iax2). SIPpeers no longer exists since Asterisk 21;
/// use PjSipShowEndpointsAction.
/// </summary>
[VerbaraMapping("PeerEntry")]
public sealed class PeerEntryEvent : ResponseEvent
{
    public string? ChannelType { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectUserName { get; set; }
    public string? ChanObjectType { get; set; }
    public string? IpAddress { get; set; }
    public int? IpPort { get; set; }
    public int? Port { get; set; }
    public bool? Dynamic { get; set; }
    public bool? NatSupport { get; set; }
    public bool? ForceRport { get; set; }
    public bool? VideoSupport { get; set; }
    public bool? TextSupport { get; set; }
    public bool? Acl { get; set; }
    public string? Status { get; set; }
    public string? RealtimeDevice { get; set; }
    public bool? Trunk { get; set; }
    public string? Encryption { get; set; }
    public string? AutoComedia { get; set; }
    public string? AutoForcerport { get; set; }
    public string? Comedia { get; set; }
    public string? Description { get; set; }
    public string? Accountcode { get; set; }
}

