using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowAors")]
public sealed class PJSipShowAorsAction : ManagerAction, IEventGeneratingAction
{
}
