using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PresenceStateList")]
public sealed class PresenceStateListAction : ManagerAction, IEventGeneratingAction
{
}
