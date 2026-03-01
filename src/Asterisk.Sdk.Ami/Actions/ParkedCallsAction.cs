using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ParkedCalls")]
public sealed class ParkedCallsAction : ManagerAction, IEventGeneratingAction
{
}

