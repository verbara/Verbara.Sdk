using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MWIUpdate")]
public sealed class MWIUpdateAction : ManagerAction
{
    public string? Mailbox { get; set; }
    public int? OldMessages { get; set; }
    public int? NewMessages { get; set; }
}

