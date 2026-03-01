using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Queues;

/// <summary>
/// Tracks all Asterisk queues, members and callers in real-time.
/// </summary>
public sealed class QueueManager
{
    private readonly ConcurrentDictionary<string, AsteriskQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public event Action<AsteriskQueue>? QueueUpdated;
    public event Action<string, AsteriskQueueMember>? MemberAdded;
    public event Action<string, AsteriskQueueMember>? MemberRemoved;
    public event Action<string, AsteriskQueueEntry>? CallerJoined;
    public event Action<string, AsteriskQueueEntry>? CallerLeft;

    public QueueManager(ILogger logger) => _logger = logger;

    public IReadOnlyCollection<AsteriskQueue> Queues => _queues.Values.ToList().AsReadOnly();

    public AsteriskQueue? GetByName(string name) => _queues.GetValueOrDefault(name);

    /// <summary>Handle QueueParams event (queue configuration snapshot).</summary>
    public void OnQueueParams(string queueName, int max, string? strategy, int calls, int holdTime, int talkTime, int completed, int abandoned)
    {
        var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
        queue.Max = max;
        queue.Strategy = strategy;
        queue.Calls = calls;
        queue.HoldTime = holdTime;
        queue.TalkTime = talkTime;
        queue.Completed = completed;
        queue.Abandoned = abandoned;
        QueueUpdated?.Invoke(queue);
    }

    /// <summary>Handle QueueMemberAdded event.</summary>
    public void OnMemberAdded(string queueName, string iface, string? memberName, int penalty, bool paused, int status)
    {
        var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
        var member = new AsteriskQueueMember
        {
            Interface = iface,
            MemberName = memberName,
            Penalty = penalty,
            Paused = paused,
            Status = (QueueMemberState)status
        };
        queue.Members.RemoveAll(m => m.Interface == iface);
        queue.Members.Add(member);
        MemberAdded?.Invoke(queueName, member);
    }

    /// <summary>Handle QueueMemberRemoved event.</summary>
    public void OnMemberRemoved(string queueName, string iface)
    {
        if (_queues.TryGetValue(queueName, out var queue))
        {
            var member = queue.Members.FirstOrDefault(m => m.Interface == iface);
            if (member is not null)
            {
                queue.Members.Remove(member);
                MemberRemoved?.Invoke(queueName, member);
            }
        }
    }

    /// <summary>Handle QueueMemberPaused event.</summary>
    public void OnMemberPaused(string queueName, string iface, bool paused, string? reason = null)
    {
        if (_queues.TryGetValue(queueName, out var queue))
        {
            var member = queue.Members.FirstOrDefault(m => m.Interface == iface);
            if (member is not null)
            {
                member.Paused = paused;
                member.PausedReason = reason;
            }
        }
    }

    /// <summary>Handle QueueCallerJoin event.</summary>
    public void OnCallerJoined(string queueName, string channel, string? callerId, int position)
    {
        var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
        var entry = new AsteriskQueueEntry
        {
            Channel = channel,
            CallerId = callerId,
            Position = position,
            JoinedAt = DateTimeOffset.UtcNow
        };
        queue.Entries.Add(entry);
        CallerJoined?.Invoke(queueName, entry);
    }

    /// <summary>Handle QueueCallerLeave event.</summary>
    public void OnCallerLeft(string queueName, string channel)
    {
        if (_queues.TryGetValue(queueName, out var queue))
        {
            var entry = queue.Entries.FirstOrDefault(e => e.Channel == channel);
            if (entry is not null)
            {
                queue.Entries.Remove(entry);
                CallerLeft?.Invoke(queueName, entry);
            }
        }
    }

    public void Clear() => _queues.Clear();
}

/// <summary>Represents a live Asterisk queue.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain term")]
public sealed class AsteriskQueue
{
    public string Name { get; init; } = string.Empty;
    public int Max { get; set; }
    public string? Strategy { get; set; }
    public int Calls { get; set; }
    public int HoldTime { get; set; }
    public int TalkTime { get; set; }
    public int Completed { get; set; }
    public int Abandoned { get; set; }
    public List<AsteriskQueueMember> Members { get; } = [];
    public List<AsteriskQueueEntry> Entries { get; } = [];
}

/// <summary>Represents a member of a queue.</summary>
public sealed class AsteriskQueueMember
{
    public string Interface { get; init; } = string.Empty;
    public string? MemberName { get; set; }
    public bool Paused { get; set; }
    public string? PausedReason { get; set; }
    public int Penalty { get; set; }
    public int CallsTaken { get; set; }
    public QueueMemberState Status { get; set; }
}

/// <summary>Represents a caller waiting in a queue.</summary>
public sealed class AsteriskQueueEntry
{
    public string Channel { get; init; } = string.Empty;
    public string? CallerId { get; set; }
    public int Position { get; set; }
    public DateTimeOffset JoinedAt { get; init; }
}
