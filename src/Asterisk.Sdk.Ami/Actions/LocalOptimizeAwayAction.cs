using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("LocalOptimizeAway")]
public sealed class LocalOptimizeAwayAction : ManagerAction
{
    public string? Channel { get; set; }
}

