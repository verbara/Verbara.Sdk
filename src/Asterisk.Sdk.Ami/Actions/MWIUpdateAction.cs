using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("MWIUpdate")]
public sealed class MWIUpdateAction : ManagerAction
{
    public string? Mailbox { get; set; }
    public int? OldMessages { get; set; }
    public int? NewMessages { get; set; }
}

