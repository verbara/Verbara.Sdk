using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ModuleCheck")]
public sealed class ModuleCheckAction : ManagerAction
{
    public string? Module { get; set; }
}

