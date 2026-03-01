using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueEntry")]
public sealed class QueueEntryEvent : ResponseEvent
{
    public string? Queue { get; set; }
    public int? Position { get; set; }
    public string? Channel { get; set; }
    public string? CallerId { get; set; }
    public long? Wait { get; set; }
}

