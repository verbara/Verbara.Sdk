using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueEntry")]
public sealed class QueueEntryEvent : ResponseEvent
{
    public string? Queue { get; set; }
    public int? Position { get; set; }
    public string? Channel { get; set; }
    public string? CallerId { get; set; }
    public long? Wait { get; set; }
}

