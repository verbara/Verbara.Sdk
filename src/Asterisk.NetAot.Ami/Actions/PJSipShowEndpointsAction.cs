using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("PJSIPShowEndpoints")]
public sealed class PJSipShowEndpointsAction : ManagerAction, IEventGeneratingAction
{
}

