using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Agents")]
public sealed class AgentsAction : ManagerAction, IEventGeneratingAction
{
}

