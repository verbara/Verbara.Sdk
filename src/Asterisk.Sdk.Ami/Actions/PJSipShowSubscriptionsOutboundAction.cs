using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowSubscriptionsOutbound")]
public sealed class PJSipShowSubscriptionsOutboundAction : ManagerAction, IEventGeneratingAction
{
}
