using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("CoreShowChannelMap")]
public sealed class CoreShowChannelMapAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
}
