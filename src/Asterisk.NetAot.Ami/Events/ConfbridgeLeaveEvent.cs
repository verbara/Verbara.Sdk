using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeLeave")]
public sealed class ConfbridgeLeaveEvent : ConfbridgeEventBase
{
}

