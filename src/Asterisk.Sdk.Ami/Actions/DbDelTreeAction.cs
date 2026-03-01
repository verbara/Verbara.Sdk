using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DBDelTree")]
public sealed class DbDelTreeAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

