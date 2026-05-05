using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("MailboxStatus")]
public sealed class MailboxStatusResponse : ManagerResponse
{
    public string? Mailbox { get; set; }
    public bool? Waiting { get; set; }
}

