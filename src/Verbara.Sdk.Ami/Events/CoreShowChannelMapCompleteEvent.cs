using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

/// <summary>Completion event for CoreShowChannelMap action. Asterisk 20+.</summary>
[VerbaraMapping("CoreShowChannelMapComplete")]
public sealed class CoreShowChannelMapCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
