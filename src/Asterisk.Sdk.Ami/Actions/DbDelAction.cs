using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DBDel")]
public sealed class DbDelAction : ManagerAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

