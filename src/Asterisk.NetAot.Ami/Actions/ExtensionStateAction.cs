using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ExtensionState")]
public sealed class ExtensionStateAction : ManagerAction
{
    public string? Exten { get; set; }
    public string? Context { get; set; }
}

