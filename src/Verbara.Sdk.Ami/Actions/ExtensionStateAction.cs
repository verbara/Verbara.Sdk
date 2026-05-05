using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ExtensionState")]
public sealed class ExtensionStateAction : ManagerAction
{
    public string? Exten { get; set; }
    public string? Context { get; set; }
}

