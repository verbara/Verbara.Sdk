using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("VoicemailRefresh")]
public sealed class VoicemailRefreshAction : ManagerAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
}
