using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("PriEvent")]
public sealed class PriEventEvent : ManagerEvent
{
    public string? PriEvent { get; set; }
    public int? PriEventCode { get; set; }
    public string? DChannel { get; set; }
    public int? Span { get; set; }
}

