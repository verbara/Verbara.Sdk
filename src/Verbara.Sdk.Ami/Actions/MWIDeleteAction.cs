using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MWIDelete")]
public sealed class MWIDeleteAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

