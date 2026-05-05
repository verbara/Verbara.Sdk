using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

/// <summary>Raised when a deadlock is detected. Only with DETECT_DEADLOCKS compile flag. Asterisk 20+.</summary>
[VerbaraMapping("DeadlockStart")]
public sealed class DeadlockStartEvent : ManagerEvent
{
    public string? Mutex { get; set; }
}
