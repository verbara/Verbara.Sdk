using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueLog")]
public sealed class QueueLogAction : ManagerAction
{
    public string? Interface { get; set; }
    public string? Queue { get; set; }
    public string? UniqueId { get; set; }
    public string? Event { get; set; }
    public string? Message { get; set; }
}

