using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SIPShowPeer")]
public sealed class SipShowPeerAction : ManagerAction
{
    public string? Peer { get; set; }
}

