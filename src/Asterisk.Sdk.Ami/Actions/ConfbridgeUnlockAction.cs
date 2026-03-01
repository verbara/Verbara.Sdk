using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeUnlock")]
public sealed class ConfbridgeUnlockAction : ManagerAction
{
    public string? Conference { get; set; }
}

