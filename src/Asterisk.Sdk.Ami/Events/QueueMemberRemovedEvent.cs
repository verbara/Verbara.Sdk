using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueMemberRemoved")]
public sealed class QueueMemberRemovedEvent : QueueMemberEventBase
{
}
