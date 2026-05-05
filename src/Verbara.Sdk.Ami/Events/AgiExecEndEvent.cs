using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgiExecEnd")]
public sealed class AgiExecEndEvent : ManagerEvent
{
    public string? Linkedid { get; set; }
    public string? Language { get; set; }
}

