using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeLock")]
public sealed class ConfbridgeLockAction : ManagerAction
{
    public string? Conference { get; set; }
}

