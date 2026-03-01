using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("OriginateFailure")]
public sealed class OriginateFailureEvent : ManagerEvent
{
}

