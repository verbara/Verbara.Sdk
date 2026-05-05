using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DBPut")]
public sealed class DbPutAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Val { get; set; }
    public string? Value { get; set; }
}

