using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 removed the BridgeExec event; no supported Asterisk sends it. Use BridgeCreateEvent, BridgeEnterEvent and BridgeLeaveEvent. Removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("BridgeExec")]
public sealed class BridgeExecEvent : ManagerEvent
{
    public string? Response { get; set; }
    public string? Reason { get; set; }
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
}

