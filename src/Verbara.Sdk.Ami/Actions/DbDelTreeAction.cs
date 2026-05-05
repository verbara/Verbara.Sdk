using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DBDelTree")]
public sealed class DbDelTreeAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

