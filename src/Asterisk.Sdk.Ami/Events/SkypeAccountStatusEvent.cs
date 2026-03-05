using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeAccountStatus")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeAccountStatusEvent : ManagerEvent
{
    public string? Username { get; set; }
    public string? Status { get; set; }
}

