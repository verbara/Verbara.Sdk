using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("PresenceStateChange")]
public sealed class PresenceStateChangeEvent : ManagerEvent
{
    public string? Presentity { get; set; }
    public string? Status { get; set; }
    public string? Subtype { get; set; }
    public string? Message { get; set; }
}
