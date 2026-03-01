using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeChatMessage")]
public sealed class SkypeChatMessageEvent : ManagerEvent
{
    public string? To { get; set; }
    public string? From { get; set; }
    public string? Message { get; set; }
    public string? DecodedMessage { get; set; }
}

