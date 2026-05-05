using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Shutdown")]
public sealed class ShutdownEvent : ManagerEvent
{
    public string? Shutdown { get; set; }
    public bool? Restart { get; set; }
}

