using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("OriginateSuccess")]
public sealed class OriginateSuccessEvent : ManagerEvent
{
}

