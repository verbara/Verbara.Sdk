using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SendFaxStatus")]
public sealed class SendFaxStatusEvent : FaxEventBase
{
    public string? Status { get; set; }
    public string? CallerId { get; set; }
    public string? LocalStationId { get; set; }
    public string? FileName { get; set; }
}

