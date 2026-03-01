using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("LocalOptimizeAway")]
public sealed class LocalOptimizeAwayAction : ManagerAction
{
    public string? Channel { get; set; }
}

