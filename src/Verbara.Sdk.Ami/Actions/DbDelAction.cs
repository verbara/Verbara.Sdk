using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DBDel")]
public sealed class DbDelAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

