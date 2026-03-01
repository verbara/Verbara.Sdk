using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AsyncAgiEnd")]
public sealed class AsyncAgiEndEvent : ManagerEvent
{
}

