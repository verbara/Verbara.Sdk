using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("PresenceStatus")]
public sealed class PresenceStatusEvent : ManagerEvent
{
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public string? Hint { get; set; }
    public string? Status { get; set; }
    public string? Subtype { get; set; }
    public string? Message { get; set; }
}
