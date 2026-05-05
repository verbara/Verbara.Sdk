using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SkypeChatMessage")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeChatMessageEvent : ManagerEvent
{
    public string? To { get; set; }
    public string? From { get; set; }
    public string? Message { get; set; }
    public string? DecodedMessage { get; set; }
}

