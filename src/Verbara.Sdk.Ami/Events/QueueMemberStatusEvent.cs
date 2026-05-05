using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

/// <summary>
/// Fired when a queue member's device state changes.
/// Now inherits QueueMemberEventBase for full field coverage (16 fields).
/// </summary>
[VerbaraMapping("QueueMemberStatus")]
public sealed class QueueMemberStatusEvent : QueueMemberEventBase
{
}
