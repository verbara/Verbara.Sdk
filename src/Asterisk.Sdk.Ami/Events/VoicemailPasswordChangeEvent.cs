using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("VoicemailPasswordChange")]
public sealed class VoicemailPasswordChangeEvent : ManagerEvent
{
    public string? Context { get; set; }
    public string? Mailbox { get; set; }
    public string? NewPassword { get; set; }
}
