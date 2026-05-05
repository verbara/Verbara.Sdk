using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MailboxStatus")]
public sealed class MailboxStatusAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

