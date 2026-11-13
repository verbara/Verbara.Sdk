using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "No supported Asterisk emits PRIEvent; chan_dahdi emitted it in Asterisk 1.6 and no longer does in 11. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0007",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007")]
[VerbaraMapping("PriEvent")]
public sealed class PriEventEvent : ManagerEvent
{
    public string? PriEvent { get; set; }
    public int? PriEventCode { get; set; }
    public string? DChannel { get; set; }
    public int? Span { get; set; }
}

