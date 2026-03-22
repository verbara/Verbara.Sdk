using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BridgeList")]
public sealed class BridgeListAction : ManagerAction, IEventGeneratingAction
{
}
