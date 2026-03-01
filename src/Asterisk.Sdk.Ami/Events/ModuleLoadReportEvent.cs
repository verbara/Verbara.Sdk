using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ModuleLoadReport")]
public sealed class ModuleLoadReportEvent : ManagerEvent
{
    public string? ModuleLoadStatus { get; set; }
    public string? ModuleSelection { get; set; }
    public int? ModuleCount { get; set; }
}

