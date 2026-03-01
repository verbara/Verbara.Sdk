using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ModuleLoadReport")]
public sealed class ModuleLoadReportEvent : ManagerEvent
{
    public string? ModuleLoadStatus { get; set; }
    public string? ModuleSelection { get; set; }
    public int? ModuleCount { get; set; }
}

