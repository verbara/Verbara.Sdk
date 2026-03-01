using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MailboxStatus")]
public sealed class MailboxStatusAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

