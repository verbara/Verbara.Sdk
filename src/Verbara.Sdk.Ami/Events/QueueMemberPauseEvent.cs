using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

/// <summary>
/// Fired when a queue member is paused or unpaused (legacy event name).
/// Now inherits QueueMemberEventBase for full field coverage.
/// Adds Pausedreason for the specific pause reason field.
/// </summary>
[VerbaraMapping("QueueMemberPause")]
public sealed class QueueMemberPauseEvent : QueueMemberEventBase
{
    /// <summary>Reason for pause (alternate casing from Asterisk).</summary>
    public string? Pausedreason { get; set; }
}
