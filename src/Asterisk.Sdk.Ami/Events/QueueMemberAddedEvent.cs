using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueMemberAdded")]
public sealed class QueueMemberAddedEvent : QueueMemberEventBase
{
}
