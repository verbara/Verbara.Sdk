using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DBGet")]
public sealed class DbGetAction : ManagerAction, IEventGeneratingAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

