using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Queues;

/// <summary>
/// Tracks all Asterisk queues, members and callers in real-time.
/// </summary>
public sealed class QueueManager
{
    private readonly ConcurrentDictionary<string, AsteriskQueue> _queues = new();
    private readonly ILogger _logger;

    public QueueManager(ILogger logger) => _logger = logger;

    public IReadOnlyCollection<AsteriskQueue> Queues => _queues.Values.ToList().AsReadOnly();
    public AsteriskQueue? GetByName(string name) => _queues.GetValueOrDefault(name);
}

/// <summary>Represents a live Asterisk queue.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain term - represents an Asterisk call queue")]
public sealed class AsteriskQueue
{
    public string Name { get; set; } = string.Empty;
    public int MaxWaitTime { get; set; }
    public List<AsteriskQueueMember> Members { get; set; } = [];
    public List<AsteriskQueueEntry> Entries { get; set; } = [];
}

/// <summary>Represents a member of a queue.</summary>
public sealed class AsteriskQueueMember
{
    public string Interface { get; set; } = string.Empty;
    public string? MemberName { get; set; }
    public bool Paused { get; set; }
    public int Penalty { get; set; }
    public int CallsTaken { get; set; }
}

/// <summary>Represents a caller waiting in a queue.</summary>
public sealed class AsteriskQueueEntry
{
    public string Channel { get; set; } = string.Empty;
    public string? CallerId { get; set; }
    public int Position { get; set; }
    public TimeSpan WaitTime { get; set; }
}
