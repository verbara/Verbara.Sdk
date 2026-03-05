using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("PresenceStatus")]
public sealed class PresenceStatusEvent : ManagerEvent
{
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public string? Hint { get; set; }
    public string? Status { get; set; }
    public string? Subtype { get; set; }
    public string? Message { get; set; }
}
