using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeEnd")]
public sealed class ConfbridgeEndEvent : ConfbridgeEventBase
{
}

