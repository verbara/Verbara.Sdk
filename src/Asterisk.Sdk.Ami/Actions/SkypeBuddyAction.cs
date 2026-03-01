using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SkypeBuddy")]
public sealed class SkypeBuddyAction : ManagerAction
{
    public string? User { get; set; }
    public string? Buddy { get; set; }
}

