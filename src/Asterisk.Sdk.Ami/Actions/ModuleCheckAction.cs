using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ModuleCheck")]
public sealed class ModuleCheckAction : ManagerAction
{
    public string? Module { get; set; }
}

