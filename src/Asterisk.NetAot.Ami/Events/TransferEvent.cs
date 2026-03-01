using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Transfer")]
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

