using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("FAXSession")]
public sealed class FAXSessionEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? SessionNumber { get; set; }
    public string? Operation { get; set; }
    public string? State { get; set; }
}
