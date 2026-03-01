using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("MailboxCount")]
public sealed class MailboxCountResponse : ManagerResponse
{
    public string? Mailbox { get; set; }
    public int? UrgMessages { get; set; }
    public int? NewMessages { get; set; }
    public int? OldMessages { get; set; }
}

