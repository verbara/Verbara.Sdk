using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeUnmute")]
public sealed class ConfbridgeUnmuteAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

