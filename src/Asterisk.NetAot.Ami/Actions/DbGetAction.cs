using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("DBGet")]
public sealed class DbGetAction : ManagerAction, IEventGeneratingAction
{
    public string? Family { get; set; }
    public string? Key { get; set; }
}

