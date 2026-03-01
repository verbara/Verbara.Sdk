using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ShowDialplan")]
public sealed class ShowDialplanAction : ManagerAction, IEventGeneratingAction
{
}

