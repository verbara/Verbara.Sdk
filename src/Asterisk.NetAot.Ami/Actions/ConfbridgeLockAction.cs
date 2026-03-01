using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeLock")]
public sealed class ConfbridgeLockAction : ManagerAction
{
    public string? Conference { get; set; }
}

