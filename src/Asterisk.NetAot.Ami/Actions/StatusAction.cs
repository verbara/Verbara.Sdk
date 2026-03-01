using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Status")]
public sealed class StatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public string? Variables { get; set; }
}

