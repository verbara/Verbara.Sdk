using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SendFaxStatus")]
public sealed class SendFaxStatusEvent : FaxEventBase
{
    public string? Status { get; set; }
    public string? CallerId { get; set; }
    public string? LocalStationId { get; set; }
    public string? FileName { get; set; }
}

