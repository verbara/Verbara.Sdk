using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Load")]
public sealed class LoadEvent : ManagerEvent
{
    public string? Module { get; set; }
    public string? Status { get; set; }
}
