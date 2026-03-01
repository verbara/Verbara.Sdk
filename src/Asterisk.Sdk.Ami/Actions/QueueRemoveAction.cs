using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueRemove")]
public sealed class QueueRemoveAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Interface { get; set; }
}

