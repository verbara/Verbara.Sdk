using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AsyncAgiExec")]
public sealed class AsyncAgiExecEvent : ManagerEvent
{
}

