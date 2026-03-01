using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("MusicOnHoldStop")]
public sealed class MusicOnHoldStopEvent : ManagerEvent
{
}

