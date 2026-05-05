using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("FullyBooted")]
public sealed class FullyBootedEvent : ManagerEvent
{
    public string? Status { get; set; }
    public string? Lastreload { get; set; }
    public int? Uptime { get; set; }
}

