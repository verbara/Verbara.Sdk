using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("MusicOnHoldStart")]
public sealed class MusicOnHoldStartEvent : ManagerEvent
{
}

