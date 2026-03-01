using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("IAXpeerlist")]
public sealed class IaxPeerListAction : ManagerAction, IEventGeneratingAction
{
}

