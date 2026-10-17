using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "res_monitor was removed in Asterisk 21 (deprecated since 16); no supported Asterisk accepts StopMonitor. Use StopMixMonitorAction. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("StopMonitor")]
public sealed class StopMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

