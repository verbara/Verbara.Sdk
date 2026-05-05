using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgiExecStart")]
public sealed class AgiExecStartEvent : ManagerEvent
{
    public string? Linkedid { get; set; }
    public string? Language { get; set; }
}

