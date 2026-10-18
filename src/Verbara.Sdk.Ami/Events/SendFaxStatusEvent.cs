using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 10 consolidated this event into FAXStatus; use FaxStatusEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("SendFaxStatus")]
public sealed class SendFaxStatusEvent : FaxEventBase
{
    public string? Status { get; set; }
    public string? CallerId { get; set; }
    public string? LocalStationId { get; set; }
    public string? FileName { get; set; }
}

