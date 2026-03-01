using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SkypeBuddies")]
public sealed class SkypeBuddiesAction : ManagerAction, IEventGeneratingAction
{
    public string? User { get; set; }
}

