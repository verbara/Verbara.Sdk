using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SkypeChatSend")]
public sealed class SkypeChatSendAction : ManagerAction
{
    public string? Skypename { get; set; }
    public string? User { get; set; }
    public string? Message { get; set; }
}

