using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 renamed this event to MusicOnHoldStart and MusicOnHoldStop; use MusicOnHoldStartEvent and MusicOnHoldStopEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("MusicOnHold")]
public sealed class MusicOnHoldEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? ClassName { get; set; }
    public string? State { get; set; }
    public string? AccountCode { get; set; }
    public string? LinkedId { get; set; }
    public string? Language { get; set; }
}

