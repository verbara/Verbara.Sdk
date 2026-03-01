using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Agents")]
public sealed class AgentsAction : ManagerAction, IEventGeneratingAction
{
}

