using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SkypeRemoveBuddy")]
public sealed class SkypeRemoveBuddyAction : ManagerAction
{
    public string? User { get; set; }
    public string? Buddy { get; set; }
}

