using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("FAXSessionsEntry")]
public sealed class FAXSessionsEntryEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? SessionNumber { get; set; }
    public string? Operation { get; set; }
    public string? State { get; set; }
    public string? Technology { get; set; }
    public string? Files { get; set; }
}
