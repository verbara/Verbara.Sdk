using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MusicOnHoldStart")]
public sealed class MusicOnHoldStartEvent : ManagerEvent
{
}

