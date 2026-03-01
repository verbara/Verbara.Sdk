using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueueRemove")]
public sealed class QueueRemoveAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Interface { get; set; }
}

