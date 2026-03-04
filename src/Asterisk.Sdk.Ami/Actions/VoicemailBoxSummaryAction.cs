using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("VoicemailBoxSummary")]
public sealed class VoicemailBoxSummaryAction : ManagerAction, IEventGeneratingAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
}
