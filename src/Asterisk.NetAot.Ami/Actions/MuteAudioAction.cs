using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MuteAudio")]
public sealed class MuteAudioAction : ManagerAction
{
    public string? Channel { get; set; }
}

