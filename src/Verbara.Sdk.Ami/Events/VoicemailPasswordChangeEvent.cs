using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("VoicemailPasswordChange")]
public sealed class VoicemailPasswordChangeEvent : ManagerEvent
{
    public string? Context { get; set; }
    public string? Mailbox { get; set; }
    public string? NewPassword { get; set; }
}
