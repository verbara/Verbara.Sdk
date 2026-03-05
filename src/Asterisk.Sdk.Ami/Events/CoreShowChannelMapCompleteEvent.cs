using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>Completion event for CoreShowChannelMap action. Asterisk 20+.</summary>
[AsteriskMapping("CoreShowChannelMapComplete")]
public sealed class CoreShowChannelMapCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
