using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PresenceStateList")]
public sealed class PresenceStateListAction : ManagerAction, IEventGeneratingAction
{
}
