using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("CoreShowChannelMap")]
public sealed class CoreShowChannelMapAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
}
