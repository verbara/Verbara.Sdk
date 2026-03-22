using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>
/// Represents a caller waiting in a queue (from QueueStatus response).
/// Asterisk 20+ added Uniqueid, CallerIDNum/Name, ConnectedLine, Priority fields.
/// </summary>
[AsteriskMapping("QueueEntry")]
public sealed class QueueEntryEvent : ResponseEvent
{
    public string? Queue { get; set; }
    public int? Position { get; set; }
    public string? Channel { get; set; }
    public string? CallerId { get; set; }
    public long? Wait { get; set; }
    /// <summary>Unique channel ID. Asterisk 20+.</summary>
    public string? Uniqueid { get; set; }
    /// <summary>Caller ID number. Asterisk 20+.</summary>
    public string? CallerIDNum { get; set; }
    /// <summary>Caller ID name. Asterisk 20+.</summary>
    public string? CallerIDName { get; set; }
    /// <summary>Connected line number. Asterisk 20+.</summary>
    public string? ConnectedLineNum { get; set; }
    /// <summary>Connected line name. Asterisk 20+.</summary>
    public string? ConnectedLineName { get; set; }
    /// <summary>Priority in the dialplan. Asterisk 20+.</summary>
    public int? Priority { get; set; }
}
