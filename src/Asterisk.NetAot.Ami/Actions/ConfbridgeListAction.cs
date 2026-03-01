using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeList")]
public sealed class ConfbridgeListAction : ManagerAction, IEventGeneratingAction
{
    public string? Conference { get; set; }
}

