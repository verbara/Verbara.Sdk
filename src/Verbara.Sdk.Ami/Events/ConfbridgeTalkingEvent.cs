using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ConfbridgeTalking")]
public sealed class ConfbridgeTalkingEvent : ConfbridgeEventBase
{
    public bool? TalkingStatus { get; set; }
}

