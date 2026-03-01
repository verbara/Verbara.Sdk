using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MailboxCount")]
public sealed class MailboxCountAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

