using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>Raised when a deadlock is detected. Only with DETECT_DEADLOCKS compile flag. Asterisk 20+.</summary>
[AsteriskMapping("DeadlockStart")]
public sealed class DeadlockStartEvent : ManagerEvent
{
    public string? Mutex { get; set; }
}
