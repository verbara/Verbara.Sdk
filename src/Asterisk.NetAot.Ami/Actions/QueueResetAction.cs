using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueueReset")]
public sealed class QueueResetAction : ManagerAction
{
    public string? Queue { get; set; }
}

