using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SipShowRegistry")]
public sealed class SipShowRegistryAction : ManagerAction, IEventGeneratingAction
{
}

