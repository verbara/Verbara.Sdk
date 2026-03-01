using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SkypeChatSend")]
public sealed class SkypeChatSendAction : ManagerAction
{
    public string? Skypename { get; set; }
    public string? User { get; set; }
    public string? Message { get; set; }
}

