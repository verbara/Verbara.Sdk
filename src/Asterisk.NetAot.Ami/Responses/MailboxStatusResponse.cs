using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("MailboxStatus")]
public sealed class MailboxStatusResponse : ManagerResponse
{
    public string? Mailbox { get; set; }
    public bool? Waiting { get; set; }
}

