using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DBGetTree")]
public sealed class DbGetTreeAction : ManagerAction, IEventGeneratingAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}
