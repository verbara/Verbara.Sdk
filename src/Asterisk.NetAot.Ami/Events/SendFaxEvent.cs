using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SendFax")]
public sealed class SendFaxEvent : FaxEventBase
{
    public string? CallerId { get; set; }
    public string? LocalStationId { get; set; }
    public string? RemoteStationId { get; set; }
    public string? PagesTransferred { get; set; }
    public string? Resolution { get; set; }
    public string? TransferRate { get; set; }
    public string? FileName { get; set; }
    public string? AccountCode { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
}

