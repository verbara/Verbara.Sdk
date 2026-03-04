using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Live.Queues;

internal static partial class QueueManagerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE] Params: queue={QueueName} strategy={Strategy} calls={Calls} completed={Completed} abandoned={Abandoned}")]
    public static partial void Params(ILogger logger, string queueName, string? strategy, int calls, int completed, int abandoned);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE] Member added: queue={QueueName} interface={Interface} penalty={Penalty}")]
    public static partial void MemberAdded(ILogger logger, string queueName, string @interface, int penalty);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE] Member removed: queue={QueueName} interface={Interface}")]
    public static partial void MemberRemoved(ILogger logger, string queueName, string @interface);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE] Member paused: queue={QueueName} interface={Interface} paused={Paused} reason={Reason}")]
    public static partial void MemberPaused(ILogger logger, string queueName, string @interface, bool paused, string? reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE] Member status: queue={QueueName} interface={Interface} status={Status}")]
    public static partial void MemberStatus(ILogger logger, string queueName, string @interface, int status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE] Caller joined: queue={QueueName} channel={Channel} position={Position}")]
    public static partial void CallerJoined(ILogger logger, string queueName, string channel, int position);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE] Caller left: queue={QueueName} channel={Channel}")]
    public static partial void CallerLeft(ILogger logger, string queueName, string channel);
}

/// <summary>
/// Tracks all Asterisk queues, members and callers in real-time.
/// All collections use ConcurrentDictionary for thread-safe concurrent access.
/// Maintains a reverse index (member interface -> queue names) for O(1) lookup.
/// </summary>
public sealed class QueueManager
{
    private readonly ConcurrentDictionary<string, AsteriskQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _queuesByMember = new();
    private readonly ILogger _logger;

    public event Action<AsteriskQueue>? QueueUpdated;
    public event Action<string, AsteriskQueueMember>? MemberAdded;
    public event Action<string, AsteriskQueueMember>? MemberRemoved;
    public event Action<string, AsteriskQueueMember>? MemberStatusChanged;
    public event Action<string, AsteriskQueueEntry>? CallerJoined;
    public event Action<string, AsteriskQueueEntry>? CallerLeft;

    public QueueManager(ILogger logger) => _logger = logger;

    public IEnumerable<AsteriskQueue> Queues => _queues.Values;

    public int QueueCount => _queues.Count;

    public AsteriskQueue? GetByName(string name) => _queues.GetValueOrDefault(name);

    /// <summary>Get queue names where a member interface is registered. O(1) lookup.</summary>
    public IEnumerable<string> GetQueuesForMember(string memberInterface)
    {
        if (_queuesByMember.TryGetValue(memberInterface, out var queues))
            return queues.Keys;
        return [];
    }

    /// <summary>Get queue objects where a member interface is registered.</summary>
    public IEnumerable<AsteriskQueue> GetQueueObjectsForMember(string memberInterface)
    {
        if (_queuesByMember.TryGetValue(memberInterface, out var queueNames))
        {
            foreach (var name in queueNames.Keys)
            {
                if (_queues.TryGetValue(name, out var queue))
                    yield return queue;
            }
        }
    }

    /// <summary>Handle QueueParams event (queue configuration snapshot).</summary>
    public void OnQueueParams(string queueName, int max, string? strategy, int calls, int holdTime, int talkTime, int completed, int abandoned)
    {
        var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
        lock (queue.SyncRoot)
        {
            queue.Max = max;
            queue.Strategy = strategy;
            queue.Calls = calls;
            queue.HoldTime = holdTime;
            queue.TalkTime = talkTime;
            queue.Completed = completed;
            queue.Abandoned = abandoned;
        }
        QueueManagerLog.Params(_logger, queueName, strategy, calls, completed, abandoned);
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
        queue.Members[iface] = member;
        _queuesByMember.GetOrAdd(iface, _ => new()).TryAdd(queueName, 0);
        QueueManagerLog.MemberAdded(_logger, queueName, iface, penalty);
        MemberAdded?.Invoke(queueName, member);
    }

    /// <summary>Handle QueueMemberRemoved event.</summary>
    public void OnMemberRemoved(string queueName, string iface)
    {
        if (_queues.TryGetValue(queueName, out var queue)
            && queue.Members.TryRemove(iface, out var member))
        {
            if (_queuesByMember.TryGetValue(iface, out var queues))
                queues.TryRemove(queueName, out _);
            QueueManagerLog.MemberRemoved(_logger, queueName, iface);
            MemberRemoved?.Invoke(queueName, member);
        }
    }

    /// <summary>Handle QueueMemberPaused event.</summary>
    public void OnMemberPaused(string queueName, string iface, bool paused, string? reason = null)
    {
        if (_queues.TryGetValue(queueName, out var queue)
            && queue.Members.TryGetValue(iface, out var member))
        {
            member.Paused = paused;
            member.PausedReason = reason;
            QueueManagerLog.MemberPaused(_logger, queueName, iface, paused, reason);
        }
    }

    /// <summary>Handle QueueMemberStatus event (device state change).</summary>
    public void OnMemberStatusChanged(string queueName, string iface, int status)
    {
        if (_queues.TryGetValue(queueName, out var queue)
            && queue.Members.TryGetValue(iface, out var member))
        {
            member.Status = (QueueMemberState)status;
            QueueManagerLog.MemberStatus(_logger, queueName, iface, status);
            MemberStatusChanged?.Invoke(queueName, member);
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
        queue.Entries[channel] = entry;
        QueueManagerLog.CallerJoined(_logger, queueName, channel, position);
        CallerJoined?.Invoke(queueName, entry);
    }

    /// <summary>Handle QueueCallerLeave event.</summary>
    public void OnCallerLeft(string queueName, string channel)
    {
        if (_queues.TryGetValue(queueName, out var queue)
            && queue.Entries.TryRemove(channel, out var entry))
        {
            QueueManagerLog.CallerLeft(_logger, queueName, channel);
            CallerLeft?.Invoke(queueName, entry);
        }
    }

    /// <summary>Handle DeviceStateChange event. Updates member status in all queues where the device is registered.</summary>
    public void OnDeviceStateChanged(string device, string state)
    {
        var memberState = MapDeviceState(state);
        if (!_queuesByMember.TryGetValue(device, out var queueNames))
            return;
        foreach (var queueName in queueNames.Keys)
        {
            if (_queues.TryGetValue(queueName, out var queue)
                && queue.Members.TryGetValue(device, out var member))
            {
                member.Status = memberState;
                MemberStatusChanged?.Invoke(queueName, member);
            }
        }
    }

    private static QueueMemberState MapDeviceState(string state) => state.ToUpperInvariant() switch
    {
        "NOT_INUSE" => QueueMemberState.DeviceNotInUse,
        "INUSE" => QueueMemberState.DeviceInUse,
        "BUSY" => QueueMemberState.DeviceBusy,
        "INVALID" => QueueMemberState.DeviceInvalid,
        "UNAVAILABLE" => QueueMemberState.DeviceUnavailable,
        "RINGING" => QueueMemberState.DeviceRinging,
        "RINGINUSE" => QueueMemberState.DeviceRingInUse,
        "ONHOLD" => QueueMemberState.DeviceOnHold,
        _ => QueueMemberState.DeviceUnknown
    };

    /// <summary>Get members of a queue matching a predicate (lazy, zero-alloc).</summary>
    public IEnumerable<AsteriskQueueMember> GetMembersWhere(
        string queueName, Func<AsteriskQueueMember, bool> predicate)
    {
        if (_queues.TryGetValue(queueName, out var queue))
            return queue.Members.Values.Where(predicate);
        return [];
    }

    public void Clear()
    {
        _queues.Clear();
        _queuesByMember.Clear();
    }
}

/// <summary>Represents a live Asterisk queue.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain term")]
public sealed class AsteriskQueue
{
    internal readonly Lock SyncRoot = new();

    public string Name { get; init; } = string.Empty;
    public int Max { get; set; }
    public string? Strategy { get; set; }
    public int Calls { get; set; }
    public int HoldTime { get; set; }
    public int TalkTime { get; set; }
    public int Completed { get; set; }
    public int Abandoned { get; set; }
    public ConcurrentDictionary<string, AsteriskQueueMember> Members { get; } = new();
    public ConcurrentDictionary<string, AsteriskQueueEntry> Entries { get; } = new();
    public int MemberCount => Members.Count;
    public int EntryCount => Entries.Count;
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
