using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("VarSet")]
public sealed class VarSetEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
    public string? LinkedId { get; set; }
    public string? AccountCode { get; set; }
}

