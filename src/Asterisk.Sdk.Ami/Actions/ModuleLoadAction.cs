using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ModuleLoad")]
public sealed class ModuleLoadAction : ManagerAction
{
    public string? Module { get; set; }
    public string? LoadType { get; set; }
}

