using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 renamed this event to BlindTransfer and AttendedTransfer; use BlindTransferEvent and AttendedTransferEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("Transfer")]
public sealed class TransferEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? TransferMethod { get; set; }
    public string? TransferType { get; set; }
    public string? SipCallId { get; set; }
    public string? TargetChannel { get; set; }
    public string? TargetUniqueId { get; set; }
    public string? TransferExten { get; set; }
    public string? TransferContext { get; set; }
    public bool? Transfer2Parking { get; set; }
}

