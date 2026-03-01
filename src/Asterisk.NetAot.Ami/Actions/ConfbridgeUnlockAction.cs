using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeUnlock")]
public sealed class ConfbridgeUnlockAction : ManagerAction
{
    public string? Conference { get; set; }
}

