using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowEndpoints")]
public sealed class PJSipShowEndpointsAction : ManagerAction, IEventGeneratingAction
{
}

