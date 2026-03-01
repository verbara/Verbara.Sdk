using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SkypeAddBuddy")]
public sealed class SkypeAddBuddyAction : ManagerAction
{
    public string? User { get; set; }
    public string? Buddy { get; set; }
    public string? AuthMsg { get; set; }
}

