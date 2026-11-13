using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "No supported Asterisk emits Masquerade; the core last emitted it in Asterisk 11. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0007",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007")]
[VerbaraMapping("Masquerade")]
public sealed class MasqueradeEvent : ManagerEvent
{
    public string? Clone { get; set; }
    public int? CloneState { get; set; }
    public string? CloneStateDesc { get; set; }
    public string? Original { get; set; }
    public int? OriginalState { get; set; }
    public string? OriginalStateDesc { get; set; }
}

