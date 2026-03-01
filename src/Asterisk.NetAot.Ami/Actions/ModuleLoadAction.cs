using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ModuleLoad")]
public sealed class ModuleLoadAction : ManagerAction
{
    public string? Module { get; set; }
    public string? LoadType { get; set; }
}

