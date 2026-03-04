using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueMemberStatus")]
public sealed class QueueMemberStatusEvent : ManagerEvent
{
    public string? Queue { get; set; }
    public string? Interface { get; set; }
    public int? Status { get; set; }
    public string? Ringinuse { get; set; }
    public int? Wrapuptime { get; set; }
}

