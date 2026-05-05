using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SkypeBuddy")]
public sealed class SkypeBuddyAction : ManagerAction
{
    public string? User { get; set; }
    public string? Buddy { get; set; }
}

