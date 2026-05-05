using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("MailboxCount")]
public sealed class MailboxCountResponse : ManagerResponse
{
    public string? Mailbox { get; set; }
    public int? UrgMessages { get; set; }
    public int? NewMessages { get; set; }
    public int? OldMessages { get; set; }
}

