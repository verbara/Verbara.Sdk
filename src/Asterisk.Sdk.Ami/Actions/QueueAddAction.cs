using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueAdd")]
public sealed class QueueAddAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Interface { get; set; }
    public int? Penalty { get; set; }
    public bool? Paused { get; set; }
    public string? Reason { get; set; }
    public string? MemberName { get; set; }
    public string? StateInterface { get; set; }
}

