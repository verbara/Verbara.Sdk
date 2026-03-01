using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("DBPut")]
public sealed class DbPutAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Val { get; set; }
    public string? Value { get; set; }
}

