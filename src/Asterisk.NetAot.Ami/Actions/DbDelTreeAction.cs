using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("DBDelTree")]
public sealed class DbDelTreeAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

