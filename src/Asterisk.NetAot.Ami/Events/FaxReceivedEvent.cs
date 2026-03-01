using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

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

