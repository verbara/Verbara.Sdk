using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("VoicemailForward")]
public sealed class VoicemailForwardAction : ManagerAction
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
    public string? Folder { get; set; }
    public string? ID { get; set; }
    public string? ToMailbox { get; set; }
    public string? ToContext { get; set; }
    public string? ToFolder { get; set; }
}
