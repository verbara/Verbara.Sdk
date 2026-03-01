using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AsyncAgiEnd")]
public sealed class AsyncAgiEndEvent : ManagerEvent
{
}

