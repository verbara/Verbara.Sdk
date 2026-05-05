using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SendFax")]
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

