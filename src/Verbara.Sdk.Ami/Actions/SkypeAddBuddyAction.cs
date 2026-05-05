using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SkypeAddBuddy")]
public sealed class SkypeAddBuddyAction : ManagerAction
{
    public string? User { get; set; }
    public string? Buddy { get; set; }
    public string? AuthMsg { get; set; }
}

