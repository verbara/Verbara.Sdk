using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SkypeChatSend")]
public sealed class SkypeChatSendAction : ManagerAction
{
    public string? Skypename { get; set; }
    public string? User { get; set; }
    public string? Message { get; set; }
}

