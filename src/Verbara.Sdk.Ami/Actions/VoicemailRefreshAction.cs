using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("VoicemailRefresh")]
public sealed class VoicemailRefreshAction : ManagerAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
}
