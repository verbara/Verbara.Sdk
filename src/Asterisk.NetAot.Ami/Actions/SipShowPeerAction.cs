using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SIPShowPeer")]
public sealed class SipShowPeerAction : ManagerAction
{
    public string? Peer { get; set; }
}

