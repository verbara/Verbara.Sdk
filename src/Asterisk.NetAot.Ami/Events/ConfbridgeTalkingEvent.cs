using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeTalking")]
public sealed class ConfbridgeTalkingEvent : ConfbridgeEventBase
{
    public bool? TalkingStatus { get; set; }
}

