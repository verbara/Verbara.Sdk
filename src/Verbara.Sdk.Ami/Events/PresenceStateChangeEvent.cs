using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("PresenceStateChange")]
public sealed class PresenceStateChangeEvent : ManagerEvent
{
    public string? Presentity { get; set; }
    public string? Status { get; set; }
    public string? Subtype { get; set; }
    public string? Message { get; set; }
}
