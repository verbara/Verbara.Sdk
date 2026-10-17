using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 removed the ModuleLoadReport event; no supported Asterisk sends it. No replacement. Removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("ModuleLoadReport")]
public sealed class ModuleLoadReportEvent : ManagerEvent
{
    public string? ModuleLoadStatus { get; set; }
    public string? ModuleSelection { get; set; }
    public int? ModuleCount { get; set; }
}

