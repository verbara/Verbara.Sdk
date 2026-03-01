using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueReset")]
public sealed class QueueResetAction : ManagerAction
{
    public string? Queue { get; set; }
}

