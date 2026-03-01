using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SipShowRegistry")]
public sealed class SipShowRegistryAction : ManagerAction, IEventGeneratingAction
{
}

