using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Unload")]
public sealed class UnloadEvent : ManagerEvent
{
    public string? Module { get; set; }
    public string? Status { get; set; }
}
