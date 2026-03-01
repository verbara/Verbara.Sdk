using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SkypeBuddies")]
public sealed class SkypeBuddiesAction : ManagerAction, IEventGeneratingAction
{
    public string? User { get; set; }
}

