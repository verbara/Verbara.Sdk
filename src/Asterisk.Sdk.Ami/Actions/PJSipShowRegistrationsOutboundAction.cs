using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowRegistrationsOutbound")]
public sealed class PJSipShowRegistrationsOutboundAction : ManagerAction, IEventGeneratingAction
{
}
