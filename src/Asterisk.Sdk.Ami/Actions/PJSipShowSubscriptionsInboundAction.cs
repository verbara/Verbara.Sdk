using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowSubscriptionsInbound")]
public sealed class PJSipShowSubscriptionsInboundAction : ManagerAction, IEventGeneratingAction
{
}
