using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MessageSend")]
public sealed class MessageSendAction : ManagerAction
{
    public string? To { get; set; }
    public string? From { get; set; }
    public string? Body { get; set; }
    public string? Base64body { get; set; }
}

