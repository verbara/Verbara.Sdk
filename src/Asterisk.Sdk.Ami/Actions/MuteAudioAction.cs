using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("MuteAudio")]
public sealed class MuteAudioAction : ManagerAction
{
    public string? Channel { get; set; }
}

