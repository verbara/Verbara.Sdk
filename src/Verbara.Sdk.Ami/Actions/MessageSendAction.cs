using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MessageSend")]
public sealed class MessageSendAction : ManagerAction
{
    public string? To { get; set; }
    public string? From { get; set; }
    public string? Body { get; set; }
    public string? Base64body { get; set; }
}

