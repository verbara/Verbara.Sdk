using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SkypeAddBuddy")]
public sealed class SkypeAddBuddyAction : ManagerAction
{
    public string? User { get; set; }
    public string? Buddy { get; set; }
    public string? AuthMsg { get; set; }
}

