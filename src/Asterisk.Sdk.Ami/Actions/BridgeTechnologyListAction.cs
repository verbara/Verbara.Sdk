using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BridgeTechnologyList")]
public sealed class BridgeTechnologyListAction : ManagerAction, IEventGeneratingAction
{
}
