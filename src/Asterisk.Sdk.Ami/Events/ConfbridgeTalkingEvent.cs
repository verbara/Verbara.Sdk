using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ConfbridgeTalking")]
public sealed class ConfbridgeTalkingEvent : ConfbridgeEventBase
{
    public bool? TalkingStatus { get; set; }
}

