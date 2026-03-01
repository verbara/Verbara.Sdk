using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeUnmute")]
public sealed class ConfbridgeUnmuteAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

