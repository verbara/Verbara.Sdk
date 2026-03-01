using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("MailboxCount")]
public sealed class MailboxCountAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

