using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SIPShowPeer")]
public sealed class SipShowPeerAction : ManagerAction
{
    public string? Peer { get; set; }
}

