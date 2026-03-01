using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("MailboxCount")]
public sealed class MailboxCountResponse : ManagerResponse
{
    public string? Mailbox { get; set; }
    public int? UrgMessages { get; set; }
    public int? NewMessages { get; set; }
    public int? OldMessages { get; set; }
}

