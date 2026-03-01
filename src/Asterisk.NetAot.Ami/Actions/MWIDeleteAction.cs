using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MWIDelete")]
public sealed class MWIDeleteAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

