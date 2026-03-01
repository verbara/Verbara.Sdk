using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DtmfBegin")]
public sealed class DtmfBeginEvent : ManagerEvent
{
}

