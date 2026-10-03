using System.Collections.Concurrent;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.Server;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Queues;

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE] Snapshot entry dropped, the caller left after it was asked for: queue={QueueName} channel={Channel}")]
    public static partial void SnapshotEntryAfterLeave(ILogger logger, string queueName, string channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE] Queue removed: queue={QueueName}")]
    public static partial void QueueRemoved(ILogger logger, string queueName);
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

    // The snapshot windows open now: one per load reading QueueStatus. Replaced, never mutated, under _windowsLock.
    private readonly Lock _windowsLock = new();
    private QueueSnapshotWindow[] _windows = [];

    // Numbers every join, live or from a snapshot, in the order this manager handled them.
    private long _joinOrdinal;

    public event Action<AsteriskQueue>? QueueUpdated;
    public event Action<string, AsteriskQueueMember>? MemberAdded;
    public event Action<string, AsteriskQueueMember>? MemberRemoved;
    public event Action<string, AsteriskQueueMember>? MemberStatusChanged;
    public event Action<string, AsteriskQueueEntry>? CallerJoined;
    public event Action<string, AsteriskQueueEntry>? CallerLeft;

    /// <summary>
    /// Raised for each <c>VarSet</c> of <c>QUEUESTATUS</c> on a channel: app_queue's account of why <c>Queue()</c>
    /// returned (<c>TIMEOUT</c>, <c>LEAVEEMPTY</c>, <c>WITHDRAW</c>, <c>FULL</c>, <c>JOINEMPTY</c>, ...), set on the
    /// caller's channel right after its <c>QueueCallerLeave</c>. Carries the channel's Uniqueid, its name and the value.
    /// Asterisk sends <c>VarSet</c> only to an AMI user whose read classes include <c>dialplan</c>; without it this is
    /// never raised. Internal: the session layer reads it to count a visit app_queue's own timeout ended.
    /// </summary>
    internal event Action<string, string, string>? CallerQueueStatus;

    /// <summary>
    /// Raised for each <c>QueueCallerAbandon</c>: app_queue's report that it counted the caller's visit abandoned, sent
    /// just before the caller's <c>QueueCallerLeave</c> for every visit it counts so (a hang-up, its timeout, an emptied
    /// queue, a withdrawal, a redirect) and never for a caller that leaves by key. Carries the caller's Uniqueid and the
    /// queue. Asterisk sends it in the <c>agent</c> class. Internal: the session layer reads it.
    /// </summary>
    internal event Action<string, string>? CallerAbandonReported;

    /// <summary>
    /// Raised for each <c>QueueCallerLeave</c> Asterisk sends, whether or not this manager's table holds the caller:
    /// <see cref="CallerLeft"/> is raised only when it does, and a reconnect clears the table without raising it, so
    /// a leave that arrives before the reload's queue snapshot is read would otherwise reach nobody. Asterisk sends it
    /// in the <c>agent</c> class. Internal: the session layer reads it.
    /// </summary>
    internal event Action<QueueCallerLeaveReport>? CallerLeaveReported;

    /// <summary>
    /// Raised when a load's <c>QueueStatus</c> completed: Asterisk answered it to the end, it was not refused and it
    /// was not cut short. Carries who the snapshot listed and the order point of its request, so that a reader can tell
    /// a caller the snapshot did not list from one that joined after it was asked for. A snapshot that did not complete
    /// raises nothing. Raised while the snapshot's window is still open, before the load reads the agents.
    /// </summary>
    internal event Action<QueueSnapshotCompletion>? QueueSnapshotCompleted;

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
        if (!_queuesByMember.TryGetValue(memberInterface, out var queueNames))
            yield break;

        foreach (var queue in queueNames.Select(entry => _queues.GetValueOrDefault(entry.Key)).OfType<AsteriskQueue>())
            yield return queue;
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
    public void OnCallerJoined(string queueName, string channel, string? callerId, int position) =>
        Join(queueName, channel, callerId, position, fromSnapshot: false, reportedWaitSeconds: null, uniqueId: null,
            lossEpoch: null);

    /// <summary>
    /// Handle a live <c>QueueCallerJoin</c> with the caller's Uniqueid and the event-loss epoch of the event, which the
    /// entry carries (<see cref="AsteriskQueueEntry.UniqueId"/>, <see cref="AsteriskQueueEntry.LossEpoch"/>).
    /// </summary>
    internal void OnCallerJoined(string queueName, string channel, string? callerId, int position, string? uniqueId,
        EventLossEpoch lossEpoch) =>
        Join(queueName, channel, callerId, position, fromSnapshot: false, reportedWaitSeconds: null, uniqueId, lossEpoch);

    /// <summary>
    /// Handle a caller that a <c>QueueStatus</c> snapshot reports waiting in a queue, with the wait
    /// Asterisk reported for it. The entry is marked with both, see
    /// <see cref="AsteriskQueueEntry.FromSnapshot"/> and <see cref="AsteriskQueueEntry.ReportedWaitSeconds"/>.
    /// <para>
    /// Internal, and an overload rather than new parameters on the public method, because the public
    /// method's signature is shipped API. Both reach the same core, so the table, the
    /// <c>live.queue.calls.joined</c> counter, the log line, <see cref="AsteriskQueueEntry.JoinedAt"/>
    /// and <see cref="CallerJoined"/> are identical for a snapshot entry and a live join.
    /// </para>
    /// </summary>
    internal void OnCallerJoined(string queueName, string channel, string? callerId, int position,
        bool fromSnapshot, long? reportedWaitSeconds) =>
        Join(queueName, channel, callerId, position, fromSnapshot, reportedWaitSeconds, uniqueId: null, lossEpoch: null);

    /// <summary>
    /// Handle a caller that the <c>QueueStatus</c> snapshot read through <paramref name="window"/> reports waiting, as
    /// <see cref="OnCallerJoined(string, string, string?, int, bool, long?)"/> does for a snapshot entry, unless Asterisk
    /// reported that caller leaving after the snapshot was asked for and no live join followed that leave: the snapshot
    /// is then older than the leave, and applying it would put back a caller who has gone. The entry is then dropped.
    /// An entry applied is listed in the window, for the completion.
    /// </summary>
    internal void OnSnapshotEntry(QueueSnapshotWindow window, string queueName, string channel, string? uniqueId,
        string? callerId, int position, long? reportedWaitSeconds, EventLossEpoch lossEpoch)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Checked and applied under the window's gate, so a leave the pump processes meanwhile is either seen here
        // (and the entry dropped) or processed after the join (and closes it).
        lock (window.Gate)
        {
            if (window.LeftAfterRequestLocked(uniqueId, channel))
            {
                QueueManagerLog.SnapshotEntryAfterLeave(_logger, queueName, channel);
                return;
            }

            window.ListLocked(uniqueId, channel);
            Join(queueName, channel, callerId, position, fromSnapshot: true, reportedWaitSeconds, uniqueId, lossEpoch);
        }
    }

    /// <summary>
    /// Opens the window of a <c>QueueStatus</c> snapshot. Opened before the request is sent and disposed once the
    /// snapshot was read: while it is open, every leave Asterisk reports is recorded in it, and every live join after
    /// such a leave clears it.
    /// </summary>
    internal QueueSnapshotWindow OpenSnapshotWindow()
    {
        var window = new QueueSnapshotWindow(this, Interlocked.Read(ref _joinOrdinal));
        lock (_windowsLock)
        {
            _windows = [.. _windows, window];
        }

        return window;
    }

    internal void CloseSnapshotWindow(QueueSnapshotWindow window)
    {
        lock (_windowsLock)
        {
            _windows = [.. _windows.Where(w => !ReferenceEquals(w, window))];
        }
    }

    /// <summary>Raises <see cref="QueueSnapshotCompleted"/> for the snapshot read through <paramref name="window"/>.</summary>
    internal void CompleteSnapshot(QueueSnapshotWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        QueueSnapshotCompleted?.Invoke(window.Complete());
    }

    private void Join(string queueName, string channel, string? callerId, int position,
        bool fromSnapshot, long? reportedWaitSeconds, string? uniqueId, EventLossEpoch? lossEpoch)
    {
        var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
        var entry = new AsteriskQueueEntry
        {
            Channel = channel,
            CallerId = callerId,
            Position = position,
            JoinedAt = DateTimeOffset.UtcNow,
            FromSnapshot = fromSnapshot,
            ReportedWaitSeconds = reportedWaitSeconds,
            UniqueId = string.IsNullOrEmpty(uniqueId) ? null : uniqueId,
            LossEpoch = lossEpoch,
            JoinOrdinal = Interlocked.Increment(ref _joinOrdinal),
        };

        // A live join after a leave the open windows recorded: the caller is waiting again, and a snapshot that lists
        // it is no longer older than its leave.
        if (!fromSnapshot)
        {
            foreach (var window in Volatile.Read(ref _windows))
                window.OnLiveJoin(entry.UniqueId, channel);
        }

        queue.Entries[channel] = entry;
        LiveMetrics.QueueCallsJoined.Add(1);
        QueueManagerLog.CallerJoined(_logger, queueName, channel, position);
        CallerJoined?.Invoke(queueName, entry);
    }

    /// <summary>Handle QueueCallerLeave event.</summary>
    public void OnCallerLeft(string queueName, string channel)
    {
        if (_queues.TryGetValue(queueName, out var queue)
            && queue.Entries.TryRemove(channel, out var entry))
        {
            LiveMetrics.QueueCallsLeft.Add(1);
            var waitMs = (DateTimeOffset.UtcNow - entry.JoinedAt).TotalMilliseconds;
            LiveMetrics.QueueWaitTimeMs.Record(waitMs);
            QueueManagerLog.CallerLeft(_logger, queueName, channel);
            CallerLeft?.Invoke(queueName, entry);
        }
    }

    /// <summary>
    /// Handle a <c>QueueCallerLeave</c> as Asterisk reported it: recorded in every open snapshot window, then
    /// <see cref="CallerLeaveReported"/>, whether or not the table holds the caller (<see cref="OnCallerLeft"/> handles
    /// the table).
    /// </summary>
    internal void OnCallerLeaveReported(string uniqueId, string queueName, string channel, EventLossEpoch lossEpoch)
    {
        foreach (var window in Volatile.Read(ref _windows))
            window.OnLeave(uniqueId, channel);

        CallerLeaveReported?.Invoke(new QueueCallerLeaveReport(uniqueId, queueName, channel, lossEpoch));
    }

    /// <summary>Handle a <c>QueueCallerAbandon</c>: raises <see cref="CallerAbandonReported"/>.</summary>
    internal void OnCallerAbandonReported(string uniqueId, string queueName) =>
        CallerAbandonReported?.Invoke(uniqueId, queueName);

    /// <summary>Handle a <c>VarSet</c> of <c>QUEUESTATUS</c> on a channel: raises <see cref="CallerQueueStatus"/>.</summary>
    internal void OnCallerQueueStatus(string uniqueId, string channel, string status) =>
        CallerQueueStatus?.Invoke(uniqueId, channel, status);

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

    /// <summary>Remove a queue entirely from the in-memory state.</summary>
    public bool RemoveQueue(string queueName)
    {
        if (!_queues.TryRemove(queueName, out var queue))
            return false;

        // Clean up reverse member index
        foreach (var iface in queue.Members.Keys)
        {
            if (_queuesByMember.TryGetValue(iface, out var queues))
                queues.TryRemove(queueName, out _);
        }

        QueueManagerLog.QueueRemoved(_logger, queueName);
        return true;
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

    /// <summary>
    /// True when this entry came from a <c>QueueEntry</c> of a <c>QueueStatus</c> snapshot (the
    /// initial load or a post-reconnect reload), rather than from a live <c>QueueCallerJoin</c>.
    /// <para>
    /// A live join is Asterisk's report that the caller has just entered the queue. A snapshot entry
    /// only says the caller is waiting there now: it may be a caller this process already saw join, or
    /// one whose join it never saw. <see cref="JoinedAt"/> is when Live handled the entry in both
    /// cases; this flag is what tells the two apart.
    /// </para>
    /// </summary>
    internal bool FromSnapshot { get; init; }

    /// <summary>
    /// The <c>Wait</c> header of the snapshot's <c>QueueEntry</c>, in whole seconds, exactly as
    /// Asterisk sent it: how long the caller had been waiting in this queue when the snapshot was
    /// taken. <c>null</c> when the header was absent, and always <c>null</c> for a live join.
    /// <para>
    /// Live does not interpret it and does not backdate <see cref="JoinedAt"/> with it; a reader that
    /// needs the time the caller joined subtracts it from its own clock.
    /// </para>
    /// </summary>
    internal long? ReportedWaitSeconds { get; init; }

    /// <summary>
    /// The caller channel's Uniqueid, as the live join or the snapshot entry carried it; <c>null</c> when it carried
    /// none, and for an entry added through the public <see cref="QueueManager.OnCallerJoined(string, string, string?, int)"/>.
    /// </summary>
    internal string? UniqueId { get; init; }

    /// <summary>
    /// The server's event-loss epoch for this join: of the <c>QueueCallerJoin</c> event, or, for a snapshot entry, at
    /// the moment the load handled it. A later event with another epoch means the SDK may have lost events in between.
    /// <c>null</c> for an entry added through the public <see cref="QueueManager.OnCallerJoined(string, string, string?, int)"/>.
    /// </summary>
    internal EventLossEpoch? LossEpoch { get; init; }

    /// <summary>
    /// The order in which the manager handled this join among all its joins, live or from a snapshot. A join whose
    /// ordinal is above a snapshot's <see cref="QueueSnapshotCompletion.OrderPoint"/> was handled after that snapshot
    /// was asked for.
    /// </summary>
    internal long JoinOrdinal { get; init; }
}

/// <summary>A <c>QueueCallerLeave</c> as Asterisk reported it, with the event-loss epoch of the event.</summary>
internal sealed record QueueCallerLeaveReport(string UniqueId, string Queue, string Channel, EventLossEpoch LossEpoch);

/// <summary>
/// One load's <c>QueueStatus</c> snapshot, from before its request is sent until it was read: the leaves Asterisk
/// reported meanwhile (so an older snapshot does not bring a caller back), and the callers the snapshot listed.
/// </summary>
internal sealed class QueueSnapshotWindow : IDisposable
{
    private readonly QueueManager _owner;
    private readonly HashSet<string> _leftUniqueIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _leftChannels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _listedUniqueIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _listedChannels = new(StringComparer.Ordinal);

    internal QueueSnapshotWindow(QueueManager owner, long orderPoint)
    {
        _owner = owner;
        OrderPoint = orderPoint;
    }

    /// <summary>Guards the sets; held while a snapshot entry is checked and applied.</summary>
    internal Lock Gate { get; } = new();

    /// <summary>The ordinal of the last join the manager had handled when the window opened, before the request.</summary>
    internal long OrderPoint { get; }

    internal void OnLeave(string? uniqueId, string channel)
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(uniqueId))
                _leftUniqueIds.Add(uniqueId);
            if (!string.IsNullOrEmpty(channel))
                _leftChannels.Add(channel);
        }
    }

    internal void OnLiveJoin(string? uniqueId, string channel)
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(uniqueId))
                _leftUniqueIds.Remove(uniqueId);
            if (!string.IsNullOrEmpty(channel))
                _leftChannels.Remove(channel);
        }
    }

    internal bool LeftAfterRequestLocked(string? uniqueId, string channel) =>
        (!string.IsNullOrEmpty(uniqueId) && _leftUniqueIds.Contains(uniqueId))
        || (!string.IsNullOrEmpty(channel) && _leftChannels.Contains(channel));

    internal void ListLocked(string? uniqueId, string channel)
    {
        if (!string.IsNullOrEmpty(uniqueId))
            _listedUniqueIds.Add(uniqueId);
        if (!string.IsNullOrEmpty(channel))
            _listedChannels.Add(channel);
    }

    internal QueueSnapshotCompletion Complete()
    {
        lock (Gate)
        {
            return new QueueSnapshotCompletion(
                new HashSet<string>(_listedUniqueIds, StringComparer.Ordinal),
                new HashSet<string>(_listedChannels, StringComparer.Ordinal), OrderPoint);
        }
    }

    public void Dispose() => _owner.CloseSnapshotWindow(this);
}

/// <summary>
/// A <c>QueueStatus</c> snapshot that completed: whom it listed, and its order point. A caller it does not list and
/// whose join the manager handled at or before <see cref="OrderPoint"/> was not waiting when Asterisk answered.
/// </summary>
internal sealed class QueueSnapshotCompletion(
    IReadOnlySet<string> listedUniqueIds, IReadOnlySet<string> listedChannels, long orderPoint)
{
    /// <summary>The ordinal of the last join the manager had handled before the snapshot was asked for.</summary>
    internal long OrderPoint { get; } = orderPoint;

    /// <summary>Whether the snapshot listed the caller with this Uniqueid or on this channel.</summary>
    internal bool Lists(string? uniqueId, string? channel) =>
        (!string.IsNullOrEmpty(uniqueId) && listedUniqueIds.Contains(uniqueId))
        || (!string.IsNullOrEmpty(channel) && listedChannels.Contains(channel));

    /// <summary>Whether a join with <paramref name="joinOrdinal"/> was handled after the snapshot was asked for.</summary>
    internal bool JoinedAfterRequest(long joinOrdinal) => joinOrdinal > OrderPoint;
}
