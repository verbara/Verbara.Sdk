using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DBGet")]
public sealed class DbGetAction : ManagerAction, IEventGeneratingAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

