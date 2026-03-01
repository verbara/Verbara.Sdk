using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Disconnect")]
public sealed class DisconnectEvent : ManagerEvent
{
}

