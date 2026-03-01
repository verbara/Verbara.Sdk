using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueuePause")]
public sealed class QueuePauseAction : ManagerAction
{
    public string? Interface { get; set; }
    public string? Queue { get; set; }
    public bool? Paused { get; set; }
    public string? Reason { get; set; }
}

