using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SkypeAccountStatus")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeAccountStatusEvent : ManagerEvent
{
    public string? Username { get; set; }
    public string? Status { get; set; }
}

