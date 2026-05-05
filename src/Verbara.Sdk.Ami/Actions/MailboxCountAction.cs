using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MailboxCount")]
public sealed class MailboxCountAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

