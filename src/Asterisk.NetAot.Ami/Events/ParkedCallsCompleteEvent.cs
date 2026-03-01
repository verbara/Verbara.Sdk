using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ParkedCallsComplete")]
public sealed class ParkedCallsCompleteEvent : ResponseEvent
{
}

