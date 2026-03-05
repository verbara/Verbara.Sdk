using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>
/// User-defined event raised from dialplan via UserEvent() application.
/// Custom fields are available via RawFields.
/// </summary>
[AsteriskMapping("UserEvent")]
public sealed class UserEventEvent : ManagerEvent
{
    public string? UserEvent { get; set; }
    public string? Channel { get; set; }
}
