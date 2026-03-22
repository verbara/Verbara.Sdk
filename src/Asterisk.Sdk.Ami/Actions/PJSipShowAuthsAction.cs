using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowAuths")]
public sealed class PJSipShowAuthsAction : ManagerAction, IEventGeneratingAction
{
}
