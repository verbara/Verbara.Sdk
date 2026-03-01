using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ParkedCalls")]
public sealed class ParkedCallsAction : ManagerAction, IEventGeneratingAction
{
}

