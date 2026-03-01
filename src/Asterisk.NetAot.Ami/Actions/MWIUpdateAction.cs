using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MWIUpdate")]
public sealed class MWIUpdateAction : ManagerAction
{
    public string? Mailbox { get; set; }
    public int? OldMessages { get; set; }
    public int? NewMessages { get; set; }
}

