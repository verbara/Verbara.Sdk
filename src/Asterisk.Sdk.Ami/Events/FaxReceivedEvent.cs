using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("FaxReceived")]
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

