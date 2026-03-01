using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DBPut")]
public sealed class DbPutAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Val { get; set; }
    public string? Value { get; set; }
}

