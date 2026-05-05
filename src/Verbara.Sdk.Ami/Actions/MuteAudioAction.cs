using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MuteAudio")]
public sealed class MuteAudioAction : ManagerAction
{
    public string? Channel { get; set; }
}

