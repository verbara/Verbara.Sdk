using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SkypeBuddies")]
public sealed class SkypeBuddiesAction : ManagerAction, IEventGeneratingAction
{
    public string? User { get; set; }
}

