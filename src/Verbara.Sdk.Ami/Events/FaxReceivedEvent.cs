using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 renamed this event to ReceiveFAX; use ReceiveFaxEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("FaxReceived")]
public sealed class FaxReceivedEvent : FaxEventBase
{
    public string? CallerId { get; set; }
    public string? RemoteStationId { get; set; }
    public string? LocalStationId { get; set; }
    public int? PagesTransferred { get; set; }
    public int? Resolution { get; set; }
    public int? TransferRate { get; set; }
    public string? Filename { get; set; }
}

