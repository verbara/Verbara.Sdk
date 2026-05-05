using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("VoicemailUserStatus")]
public sealed class VoicemailUserStatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
}
