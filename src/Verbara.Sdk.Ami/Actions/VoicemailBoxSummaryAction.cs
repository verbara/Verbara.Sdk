using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("VoicemailBoxSummary")]
public sealed class VoicemailBoxSummaryAction : ManagerAction, IEventGeneratingAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
}
