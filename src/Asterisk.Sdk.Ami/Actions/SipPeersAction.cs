using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SIPPeers")]
public sealed class SipPeersAction : ManagerAction, IEventGeneratingAction
{
}

