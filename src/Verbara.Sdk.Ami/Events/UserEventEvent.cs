using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

/// <summary>
/// User-defined event raised from dialplan via UserEvent() application.
/// Custom fields are available via RawFields.
/// </summary>
[VerbaraMapping("UserEvent")]
public sealed class UserEventEvent : ManagerEvent
{
    public string? UserEvent { get; set; }
    public string? Channel { get; set; }
}
