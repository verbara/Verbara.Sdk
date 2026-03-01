using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AsyncAgiStart")]
public sealed class AsyncAgiStartEvent : ManagerEvent
{
}

