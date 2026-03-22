using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>
/// Fired when a queue member's device state changes.
/// Now inherits QueueMemberEventBase for full field coverage (16 fields).
/// </summary>
[AsteriskMapping("QueueMemberStatus")]
public sealed class QueueMemberStatusEvent : QueueMemberEventBase
{
}
