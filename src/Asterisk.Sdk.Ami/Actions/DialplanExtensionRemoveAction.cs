using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DialplanExtensionRemove")]
public sealed class DialplanExtensionRemoveAction : ManagerAction
{
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public int? Priority { get; set; }
}
