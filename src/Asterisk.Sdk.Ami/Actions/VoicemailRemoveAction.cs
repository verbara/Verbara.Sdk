using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("VoicemailRemove")]
public sealed class VoicemailRemoveAction : ManagerAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
    public string? Folder { get; set; }
    public string? ID { get; set; }
}
