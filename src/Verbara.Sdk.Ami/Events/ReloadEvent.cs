using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Reload")]
public sealed class ReloadEvent : ManagerEvent
{
    public string? Module { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}

