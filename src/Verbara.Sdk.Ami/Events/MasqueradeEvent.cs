using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Masquerade")]
public sealed class MasqueradeEvent : ManagerEvent
{
    public string? Clone { get; set; }
    public int? CloneState { get; set; }
    public string? CloneStateDesc { get; set; }
    public string? Original { get; set; }
    public int? OriginalState { get; set; }
    public string? OriginalStateDesc { get; set; }
}

