using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SIPPeers")]
public sealed class SipPeersAction : ManagerAction, IEventGeneratingAction
{
}

