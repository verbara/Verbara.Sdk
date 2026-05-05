using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ModuleLoadReport")]
public sealed class ModuleLoadReportEvent : ManagerEvent
{
    public string? ModuleLoadStatus { get; set; }
    public string? ModuleSelection { get; set; }
    public int? ModuleCount { get; set; }
}

