using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("MailboxStatus")]
public sealed class MailboxStatusAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

