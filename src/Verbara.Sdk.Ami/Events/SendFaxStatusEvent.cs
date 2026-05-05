using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SendFaxStatus")]
public sealed class SendFaxStatusEvent : FaxEventBase
{
    public string? Status { get; set; }
    public string? CallerId { get; set; }
    public string? LocalStationId { get; set; }
    public string? FileName { get; set; }
}

