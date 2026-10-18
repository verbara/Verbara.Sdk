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

    [LoggerMessage(Level = LogLevel.Debug, Message = "[QUEUE] Caller removed, its leave was not observed: queue={QueueName} channel={Channel} reason={Reason}")]
    public static partial void CallerRemovedUnobserved(ILogger logger, string queueName, string channel, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[QUEUE] Member removed, the completed queue snapshot no longer lists it: queue={QueueName} interface={Interface}")]
    public static partial void MemberRemovedBySnapshot(ILogger logger, string queueName, string @interface);
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
    /// <see cref="CallerLeft"/> is raised only when it does, and is raised as well for a caller removed with no leave
    /// observed, so it cannot tell the session layer that Asterisk reported one. Asterisk sends it in the <c>agent</c>
    /// class. Internal: the session layer reads it.
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
        // A member added outside a snapshot while a snapshot window is open: that snapshot may have been answered before
        // it, so its silence about this member does not remove it.
        foreach (var window in Volatile.Read(ref _windows))
            window.OnLiveMember(queueName, iface);

        AddMember(queueName, iface, memberName, penalty, paused, status);
    }

    /// <summary>
    /// Handle a queue's <c>QueueParams</c> as the <c>QueueStatus</c> snapshot read through <paramref name="window"/>
    /// reports it: as <see cref="OnQueueParams"/>, and the queue is listed in the window, so the completion keeps it.
    /// </summary>
    internal void OnSnapshotQueueParams(QueueSnapshotWindow window, string queueName, int max, string? strategy,
        int calls, int holdTime, int talkTime, int completed, int abandoned)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ListQueue(queueName);
        OnQueueParams(queueName, max, strategy, calls, holdTime, talkTime, completed, abandoned);
    }

    /// <summary>
    /// Handle a <c>QueueMember</c> as the <c>QueueStatus</c> snapshot read through <paramref name="window"/> reports it:
    /// as <see cref="OnMemberAdded"/>, and the member is listed in the window, so the completion keeps it.
    /// </summary>
    internal void OnSnapshotMember(QueueSnapshotWindow window, string queueName, string iface, string? memberName,
        int penalty, bool paused, int status)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ListMember(queueName, iface);
        AddMember(queueName, iface, memberName, penalty, paused, status);
    }

    private void AddMember(string queueName, string iface, string? memberName, int penalty, bool paused, int status)
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

            window.ListLocked(queueName, uniqueId, channel);
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

    /// <summary>
    /// Applies the completed <c>QueueStatus</c> snapshot read through <paramref name="window"/> to the table, then raises
    /// <see cref="QueueSnapshotCompleted"/> for it. Called only for a snapshot Asterisk answered to the end.
    /// <list type="bullet">
    /// <item>A held caller the snapshot does not list in its queue, and whose join was handled before the snapshot was
    /// asked for, is removed: counted left, with no wait sample, and announced with <see cref="CallerLeft"/>, its leave
    /// marked as not observed (<see cref="AsteriskQueueEntry.LeaveNotObserved"/>).</item>
    /// <item>A queue the snapshot does not list loses those callers the same way, and is removed once it holds no caller
    /// and no member added outside the snapshot while it was read. No event is raised for the queue.</item>
    /// <item>A member a listed queue holds and the snapshot does not list is removed, unless it was added outside the
    /// snapshot while it was read. No event is raised for it, as none was when a reconnect cleared the table.</item>
    /// </list>
    /// A caller who joined after the request, and a member added after it, are kept: the snapshot could not list them.
    /// </summary>
    internal void CompleteSnapshot(QueueSnapshotWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var view = window.TableView();
        foreach (var queue in _queues.Values)
            ApplySnapshot(queue, view);

        QueueSnapshotCompleted?.Invoke(window.Complete());
    }

    private void ApplySnapshot(AsteriskQueue queue, QueueSnapshotTableView view)
    {
        var listed = view.ListsQueue(queue.Name);
        List<AsteriskQueueEntry>? gone = null;
        List<string>? membersGone = null;
        var removeQueue = false;
        lock (queue.SyncRoot)
        {
            foreach (var (key, entry) in queue.Entries)
            {
                if (entry.JoinOrdinal > view.OrderPoint
                    || (listed && view.ListsCaller(queue.Name, entry.UniqueId, entry.Channel)))
                {
                    continue;
                }

                if (queue.Entries.TryRemove(new KeyValuePair<string, AsteriskQueueEntry>(key, entry)))
                    (gone ??= []).Add(entry);
            }

            foreach (var iface in queue.Members.Keys)
            {
                if ((listed && view.ListsMember(queue.Name, iface)) || view.MemberAddedLive(queue.Name, iface))
                    continue;

                if (listed && queue.Members.TryRemove(iface, out _))
                    (membersGone ??= []).Add(iface);
            }

            removeQueue = !listed && queue.Entries.IsEmpty
                && !queue.Members.Keys.Any(iface => view.MemberAddedLive(queue.Name, iface));
        }

        if (gone is not null)
        {
            foreach (var entry in gone)
                LeftUnobserved(queue.Name, entry, "snapshot", announce: true);
        }

        if (membersGone is not null)
        {
            foreach (var iface in membersGone)
            {
                if (_queuesByMember.TryGetValue(iface, out var queues))
                    queues.TryRemove(queue.Name, out _);
                QueueManagerLog.MemberRemovedBySnapshot(_logger, queue.Name, iface);
            }
        }

        if (removeQueue && _queues.TryRemove(new KeyValuePair<string, AsteriskQueue>(queue.Name, queue)))
        {
            ForgetMembers(queue);
            QueueManagerLog.QueueRemoved(_logger, queue.Name);
        }
    }

    /// <summary>
    /// A caller removed from the table without an observed leave: counted left with no wait sample, and marked
    /// (<see cref="AsteriskQueueEntry.LeaveNotObserved"/>). <see cref="CallerLeft"/> is raised when
    /// <paramref name="announce"/> is set; <see cref="CallerLeaveReported"/> never is, since Asterisk sent no leave.
    /// </summary>
    private void LeftUnobserved(string queueName, AsteriskQueueEntry entry, string reason, bool announce)
    {
        entry.MarkLeaveNotObserved();
        LiveMetrics.QueueCallsLeft.Add(1);
        QueueManagerLog.CallerRemovedUnobserved(_logger, queueName, entry.Channel, reason);
        if (announce)
            CallerLeft?.Invoke(queueName, entry);
    }

    /// <summary>
    /// A channel reconciliation removed a channel Asterisk no longer has: every caller waiting on it (by its Uniqueid,
    /// or by its name for an entry that carries no Uniqueid) leaves the table, counted left with no wait sample and
    /// announced with <see cref="CallerLeft"/>, its leave marked as not observed; never <see cref="CallerLeaveReported"/>.
    /// </summary>
    internal void OnChannelRemovedByReconciliation(string uniqueId, string channelName)
    {
        foreach (var queue in _queues.Values)
        {
            List<AsteriskQueueEntry>? gone = null;
            lock (queue.SyncRoot)
            {
                foreach (var (key, entry) in queue.Entries)
                {
                    var onChannel = entry.UniqueId is { } held
                        ? string.Equals(held, uniqueId, StringComparison.Ordinal)
                        : string.Equals(entry.Channel, channelName, StringComparison.Ordinal);
                    if (onChannel && queue.Entries.TryRemove(new KeyValuePair<string, AsteriskQueueEntry>(key, entry)))
                        (gone ??= []).Add(entry);
                }
            }

            if (gone is null)
                continue;

            foreach (var entry in gone)
                LeftUnobserved(queue.Name, entry, "channel-reconciled", announce: true);
        }
    }

    /// <summary>
    /// The core of every join, live or from a snapshot. A caller the table does not hold is a new insertion: the entry
    /// is stored, <c>live.queue.calls.joined</c> advances, and <see cref="CallerJoined"/> carries the stored entry. A
    /// caller it holds already (by Uniqueid, else by channel) is a replacement: the held entry is kept with its
    /// <see cref="AsteriskQueueEntry.JoinedAt"/>, <see cref="AsteriskQueueEntry.FromSnapshot"/> and
    /// <see cref="AsteriskQueueEntry.ReportedWaitSeconds"/>, only its position follows the join, nothing is counted, and
    /// <see cref="CallerJoined"/> carries the join as it was reported, which the session layer reads to tell a visit it
    /// holds from a new one. A live join over a held caller also moves the held entry's
    /// <see cref="AsteriskQueueEntry.JoinOrdinal"/>, since Asterisk reported it joining after any snapshot asked earlier.
    /// </summary>
    private void Join(string queueName, string channel, string? callerId, int position,
        bool fromSnapshot, long? reportedWaitSeconds, string? uniqueId, EventLossEpoch? lossEpoch)
    {
        var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
        var reported = new AsteriskQueueEntry
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
                window.OnLiveJoin(reported.UniqueId, channel);
        }

        AsteriskQueueEntry? displaced = null;
        bool inserted;
        lock (queue.SyncRoot)
        {
            var held = FindHeldLocked(queue, reported.UniqueId, channel);
            if (held is not null)
            {
                inserted = false;
                held.Position = position;
                if (!fromSnapshot)
                    held.JoinOrdinal = reported.JoinOrdinal;

                // Held under another channel name (renamed since): re-keyed under the name the join reports, so the
                // caller's leave finds it, with everything else kept.
                if (!string.Equals(held.Channel, channel, StringComparison.Ordinal))
                {
                    queue.Entries.TryRemove(new KeyValuePair<string, AsteriskQueueEntry>(held.Channel, held));
                    displaced = queue.Entries.GetValueOrDefault(channel);
                    queue.Entries[channel] = held.RenamedTo(channel);
                }
            }
            else
            {
                inserted = true;
                // Another caller held under this channel name (one whose Uniqueid differs): it is no longer there.
                displaced = queue.Entries.GetValueOrDefault(channel);
                queue.Entries[channel] = reported;
            }
        }

        if (displaced is not null)
            LeftUnobserved(queueName, displaced, "displaced", announce: true);

        if (inserted)
            LiveMetrics.QueueCallsJoined.Add(1);
        QueueManagerLog.CallerJoined(_logger, queueName, channel, position);
        CallerJoined?.Invoke(queueName, reported);
    }

    /// <summary>
    /// The entry <paramref name="queue"/> holds for this caller, under its lock: by channel when that entry is the same
    /// caller (same Uniqueid, or either carries none), else by Uniqueid under another channel name.
    /// </summary>
    private static AsteriskQueueEntry? FindHeldLocked(AsteriskQueue queue, string? uniqueId, string channel)
    {
        if (queue.Entries.TryGetValue(channel, out var byChannel)
            && (byChannel.UniqueId is null || uniqueId is null
                || string.Equals(byChannel.UniqueId, uniqueId, StringComparison.Ordinal)))
        {
            return byChannel;
        }

        if (uniqueId is null)
            return null;

        foreach (var entry in queue.Entries.Values)
        {
            if (string.Equals(entry.UniqueId, uniqueId, StringComparison.Ordinal))
                return entry;
        }

        return null;
    }

    /// <summary>Handle QueueCallerLeave event.</summary>
    public void OnCallerLeft(string queueName, string channel)
    {
        if (!_queues.TryGetValue(queueName, out var queue))
            return;

        AsteriskQueueEntry? entry;
        bool removed;
        lock (queue.SyncRoot)
        {
            removed = queue.Entries.TryRemove(channel, out entry);
        }

        if (removed && entry is not null)
        {
            LiveMetrics.QueueCallsLeft.Add(1);
            var waitMs = (DateTimeOffset.UtcNow - entry.JoinedAt).TotalMilliseconds + ReportedWaitMilliseconds(entry);
            LiveMetrics.QueueWaitTimeMs.Record(waitMs);
            QueueManagerLog.CallerLeft(_logger, queueName, channel);
            CallerLeft?.Invoke(queueName, entry);
        }
    }

    /// <summary>
    /// The part of a caller's wait that passed before Live held it: for an entry a <c>QueueStatus</c> snapshot
    /// reported, the <c>Wait</c> Asterisk reported for it (whole seconds, so within about a second of the caller's
    /// true wait when the snapshot was taken); <c>0</c> otherwise.
    /// <para>
    /// <c>0</c> as well for a reported wait the session manager does not count either: absent, negative (Asterisk
    /// does not send one), or reaching back before the earliest instant a <see cref="DateTimeOffset"/> can hold from
    /// <see cref="AsteriskQueueEntry.JoinedAt"/>, which Asterisk cannot have measured and which would otherwise throw
    /// after the entry was removed and before its leave was raised.
    /// </para>
    /// </summary>
    private static double ReportedWaitMilliseconds(AsteriskQueueEntry entry) =>
        entry is { FromSnapshot: true, ReportedWaitSeconds: long waitSeconds and >= 0 }
        && waitSeconds <= entry.JoinedAt.UtcTicks / TimeSpan.TicksPerSecond
            ? TimeSpan.FromSeconds(waitSeconds).TotalMilliseconds
            : 0;

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
    /// <remarks>
    /// Each caller the queue held is counted in <c>live.queue.calls.left</c>, with no <c>live.queue.wait_time</c>
    /// sample since no leave was observed, so that <c>live.queue.calls.joined</c> minus <c>live.queue.calls.left</c>
    /// keeps counting the callers held. No event is raised.
    /// </remarks>
    public bool RemoveQueue(string queueName)
    {
        if (!_queues.TryRemove(queueName, out var queue))
            return false;

        List<AsteriskQueueEntry> gone = [];
        lock (queue.SyncRoot)
        {
            foreach (var (key, entry) in queue.Entries)
            {
                if (queue.Entries.TryRemove(new KeyValuePair<string, AsteriskQueueEntry>(key, entry)))
                    gone.Add(entry);
            }
        }

        foreach (var entry in gone)
            LeftUnobserved(queue.Name, entry, "queue-removed", announce: false);

        ForgetMembers(queue);
        QueueManagerLog.QueueRemoved(_logger, queueName);
        return true;
    }

    /// <summary>Drops a removed queue from the reverse member index.</summary>
    private void ForgetMembers(AsteriskQueue queue)
    {
        foreach (var iface in queue.Members.Keys)
        {
            if (_queuesByMember.TryGetValue(iface, out var queues))
                queues.TryRemove(queue.Name, out _);
        }
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
    /// Live adds it to the caller's <c>live.queue.wait_time</c> sample when the caller leaves, so the sample counts
    /// the wait Asterisk reported as well as the time Live held the caller. It does not backdate
    /// <see cref="JoinedAt"/> with it; a reader that needs the time the caller joined subtracts it from its own
    /// clock.
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
    internal long JoinOrdinal { get; set; }

    /// <summary>
    /// True once the entry left the table without an observed <c>QueueCallerLeave</c>: a completed queue snapshot no
    /// longer listed it, its queue was removed, or a channel reconciliation removed its channel. Set before
    /// <see cref="QueueManager.CallerLeft"/> is raised for it; such a removal records no <c>live.queue.wait_time</c>
    /// sample.
    /// </summary>
    internal bool LeaveNotObserved { get; private set; }

    internal void MarkLeaveNotObserved() => LeaveNotObserved = true;

    /// <summary>A copy of this entry under another channel name, with everything else kept.</summary>
    internal AsteriskQueueEntry RenamedTo(string channel) => new()
    {
        Channel = channel,
        CallerId = CallerId,
        Position = Position,
        JoinedAt = JoinedAt,
        FromSnapshot = FromSnapshot,
        ReportedWaitSeconds = ReportedWaitSeconds,
        UniqueId = UniqueId,
        LossEpoch = LossEpoch,
        JoinOrdinal = JoinOrdinal,
    };
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

    // What the snapshot listed per queue, for the table: queues by name, callers and members as (queue, key) pairs.
    // Queue names are compared as the manager keys queues (ordinal, ignoring case).
    private readonly HashSet<string> _listedQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Queue, string Key)> _listedCallers = new(QueueKeyComparer.Instance);
    private readonly HashSet<(string Queue, string Key)> _listedMembers = new(QueueKeyComparer.Instance);
    private readonly HashSet<(string Queue, string Key)> _liveMembers = new(QueueKeyComparer.Instance);

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

    internal void ListLocked(string queueName, string? uniqueId, string channel)
    {
        if (!string.IsNullOrEmpty(uniqueId))
        {
            _listedUniqueIds.Add(uniqueId);
            _listedCallers.Add((queueName, "u:" + uniqueId));
        }

        if (!string.IsNullOrEmpty(channel))
        {
            _listedChannels.Add(channel);
            _listedCallers.Add((queueName, "c:" + channel));
        }
    }

    internal void ListQueue(string queueName)
    {
        lock (Gate)
        {
            _listedQueues.Add(queueName);
        }
    }

    internal void ListMember(string queueName, string iface)
    {
        lock (Gate)
        {
            _listedQueues.Add(queueName);
            _listedMembers.Add((queueName, iface));
        }
    }

    internal void OnLiveMember(string queueName, string iface)
    {
        lock (Gate)
        {
            _liveMembers.Add((queueName, iface));
        }
    }

    /// <summary>A copy of what the snapshot listed per queue, for the table's completion.</summary>
    internal QueueSnapshotTableView TableView()
    {
        lock (Gate)
        {
            return new QueueSnapshotTableView(OrderPoint,
                new HashSet<string>(_listedQueues, StringComparer.OrdinalIgnoreCase),
                new HashSet<(string, string)>(_listedCallers, QueueKeyComparer.Instance),
                new HashSet<(string, string)>(_listedMembers, QueueKeyComparer.Instance),
                new HashSet<(string, string)>(_liveMembers, QueueKeyComparer.Instance));
        }
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
/// What a completed <c>QueueStatus</c> snapshot listed per queue, as the queue table applies it: the queues, the callers
/// of each (by Uniqueid or channel), the members of each, and the members added outside the snapshot while it was read.
/// </summary>
internal sealed class QueueSnapshotTableView(
    long orderPoint,
    IReadOnlySet<string> queues,
    IReadOnlySet<(string Queue, string Key)> callers,
    IReadOnlySet<(string Queue, string Key)> members,
    IReadOnlySet<(string Queue, string Key)> liveMembers)
{
    /// <summary>The ordinal of the last join the manager had handled before the snapshot was asked for.</summary>
    internal long OrderPoint { get; } = orderPoint;

    internal bool ListsQueue(string queueName) => queues.Contains(queueName);

    internal bool ListsCaller(string queueName, string? uniqueId, string channel) =>
        (!string.IsNullOrEmpty(uniqueId) && callers.Contains((queueName, "u:" + uniqueId)))
        || (!string.IsNullOrEmpty(channel) && callers.Contains((queueName, "c:" + channel)));

    internal bool ListsMember(string queueName, string iface) => members.Contains((queueName, iface));

    internal bool MemberAddedLive(string queueName, string iface) => liveMembers.Contains((queueName, iface));
}

/// <summary>Compares (queue, key) pairs: the queue ordinally ignoring case, as queues are keyed; the key ordinally.</summary>
internal sealed class QueueKeyComparer : IEqualityComparer<(string Queue, string Key)>
{
    public static readonly QueueKeyComparer Instance = new();

    public bool Equals((string Queue, string Key) x, (string Queue, string Key) y) =>
        StringComparer.OrdinalIgnoreCase.Equals(x.Queue, y.Queue) && StringComparer.Ordinal.Equals(x.Key, y.Key);

    public int GetHashCode((string Queue, string Key) obj) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Queue), StringComparer.Ordinal.GetHashCode(obj.Key));
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
