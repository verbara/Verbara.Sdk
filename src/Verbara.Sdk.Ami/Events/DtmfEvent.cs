using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 renamed this event to DTMFBegin and DTMFEnd; use DtmfBeginEvent and DtmfEndEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("Dtmf")]
public sealed class DtmfEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? Digit { get; set; }
    public string? Direction { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? AccountCode { get; set; }
}

