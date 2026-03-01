using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("MailboxStatus")]
public sealed class MailboxStatusResponse : ManagerResponse
{
    public string? Mailbox { get; set; }
    public bool? Waiting { get; set; }
}

