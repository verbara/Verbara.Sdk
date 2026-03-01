using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueLog")]
public sealed class QueueLogAction : ManagerAction
{
    public string? Interface { get; set; }
    public string? Queue { get; set; }
    public string? UniqueId { get; set; }
    public string? Event { get; set; }
    public string? Message { get; set; }
}

