using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("BridgeList")]
public sealed class BridgeListAction : ManagerAction, IEventGeneratingAction
{
}
