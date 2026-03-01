using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("DBDel")]
public sealed class DbDelAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

