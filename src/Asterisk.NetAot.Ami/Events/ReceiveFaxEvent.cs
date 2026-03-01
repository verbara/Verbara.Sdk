using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ReceiveFax")]
public sealed class ReceiveFaxEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? CallerId { get; set; }
    public string? RemoteStationId { get; set; }
    public string? LocalStationId { get; set; }
    public int? PagesTransferred { get; set; }
    public string? Resolution { get; set; }
    public int? TransferRate { get; set; }
    public string? FileName { get; set; }
    public string? AccountCode { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
}

