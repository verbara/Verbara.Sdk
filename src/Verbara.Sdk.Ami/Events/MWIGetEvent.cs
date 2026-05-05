using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MWIGet")]
public sealed class MWIGetEvent : ManagerEvent
{
    public string? Mailbox { get; set; }
    public int? OldMessages { get; set; }
    public int? NewMessages { get; set; }
}
