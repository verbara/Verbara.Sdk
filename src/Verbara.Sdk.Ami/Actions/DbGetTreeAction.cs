using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DBGetTree")]
public sealed class DbGetTreeAction : ManagerAction, IEventGeneratingAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}
