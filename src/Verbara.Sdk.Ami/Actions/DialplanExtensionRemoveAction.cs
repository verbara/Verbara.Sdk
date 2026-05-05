using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DialplanExtensionRemove")]
public sealed class DialplanExtensionRemoveAction : ManagerAction
{
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public int? Priority { get; set; }
}
