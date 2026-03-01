using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("IAXpeerlist")]
public sealed class IaxPeerListAction : ManagerAction, IEventGeneratingAction
{
}

