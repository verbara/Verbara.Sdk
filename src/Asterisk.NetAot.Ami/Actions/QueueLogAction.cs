using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueueLog")]
public sealed class QueueLogAction : ManagerAction
{
    public string? Interface { get; set; }
    public string? Queue { get; set; }
    public string? UniqueId { get; set; }
    public string? Event { get; set; }
    public string? Message { get; set; }
}

