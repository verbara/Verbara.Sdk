using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowRegistrationsInbound")]
public sealed class PJSipShowRegistrationsInboundAction : ManagerAction, IEventGeneratingAction
{
}
