using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MWIGet")]
public sealed class MWIGetEvent : ManagerEvent
{
    public string? Mailbox { get; set; }
    public int? OldMessages { get; set; }
    public int? NewMessages { get; set; }
}
