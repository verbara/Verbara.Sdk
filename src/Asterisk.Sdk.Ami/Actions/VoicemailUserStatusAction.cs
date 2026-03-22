using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("VoicemailUserStatus")]
public sealed class VoicemailUserStatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
}
