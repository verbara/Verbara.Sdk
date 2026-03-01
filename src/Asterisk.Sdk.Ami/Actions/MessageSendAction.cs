using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("MessageSend")]
public sealed class MessageSendAction : ManagerAction
{
    public string? To { get; set; }
    public string? From { get; set; }
    public string? Body { get; set; }
    public string? Base64body { get; set; }
}

