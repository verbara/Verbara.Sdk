using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ExtensionState")]
public sealed class ExtensionStateAction : ManagerAction
{
    public string? Exten { get; set; }
    public string? Context { get; set; }
}

