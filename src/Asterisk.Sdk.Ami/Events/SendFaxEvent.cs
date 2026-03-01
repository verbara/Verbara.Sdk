using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

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

