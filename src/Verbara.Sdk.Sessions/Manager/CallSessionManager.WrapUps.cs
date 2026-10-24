using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Sessions.Internal;

namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// One leg of a call that an agent or a queue member took, counted as connected for that agent's or member's
/// wrap-up until its end is handled. <paramref name="Key"/> is the wrap-up key the leg counts under;
/// <paramref name="OpensAtCallEnd"/> is set for a leg the agent's connect counted on a call the manager saw join no
/// queue, whose wrap-up opens when the call ends unless the member's <c>AgentComplete</c> naming a queue ends the leg
/// first; a queue leg ends at the member's <c>AgentComplete</c>.
/// </summary>
internal sealed record WrapUpLeg(string ServerId, string? Interface, string Key, string? AgentId, bool OpensAtCallEnd);

public sealed partial class CallSessionManager
{
    /// <summary>
    /// The open wrap-ups, one per key: an agent known by name (<see cref="AgentWrapUpKey"/>) or a member interface on
    /// the server that reported it (<see cref="MemberWrapUpKey"/>). Whoever removes an entry closes it, so each is
    /// closed once: a timer callback removes only its own entry, by reference, and never one that replaced it.
    /// </summary>
    private readonly ConcurrentDictionary<string, WrapUpEntry> _openWrapUps = new(StringComparer.Ordinal);

    /// <summary>
    /// How many legs each wrap-up key has connected. A leg's end opens a wrap-up only when it leaves none: with
    /// app_queue's <c>ringinuse</c> a member can finish one call while another is up.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _connectedAgentLegs = new(StringComparer.Ordinal);

    /// <summary>
    /// Every wrap-up whose timer may still be pending or running, so disposal can wait for a close already running on
    /// a pool thread. An entry leaves it once its timer has been disposed.
    /// </summary>
    private readonly ConcurrentDictionary<WrapUpEntry, byte> _wrapUpTimers = new();

    /// <summary>
    /// Held while a <see cref="CallWrapUpEvent"/> is raised, and checked against <see cref="_wrapUpsDisposed"/> under
    /// it, so no close raises on the event stream once disposal has passed it. Re-entrant: a subscriber may hand the
    /// manager more events from inside the raise.
    /// </summary>
    private readonly Lock _wrapUpRaise = new();

    private bool _wrapUpsDisposed;

    /// <summary>
    /// How long a wrap-up duration provider may take to answer before it is reported as slow. Fixed, not an option: it
    /// runs on its server's single event consumer, and the event-dispatch histogram already carries the aggregate.
    /// </summary>
    private static readonly TimeSpan WrapUpDurationProviderSlowBound = TimeSpan.FromSeconds(1);

    /// <summary>How many (server, queue, kind) cases <see cref="_warnedWrapUpDurations"/> records. Fixed, not an option.</summary>
    private const int WrapUpDurationWarnSetCapacity = 1024;

    /// <summary>
    /// The (server, queue, kind) cases of a wrap-up duration provider's misbehaviour already reported at Warning; later
    /// ones log at Debug. Never cleared: the Warning is once per case for the manager's life. Its queue is read off
    /// the wire, so it is capped at <see cref="WrapUpDurationWarnSetCapacity"/> entries: past it, new cases are not
    /// recorded and log at Debug, and one Warning says so.
    /// </summary>
    private readonly ConcurrentDictionary<(string ServerId, string Queue, WrapUpDurationAnomaly Kind), byte>
        _warnedWrapUpDurations = new();

    private int _warnedWrapUpDurationCount;
    private int _wrapUpDurationWarnSetFullReported;

    [LoggerMessage(EventId = 5, EventName = nameof(LogWrapUpOpened), Level = LogLevel.Debug,
        Message = "Wrap-up opened for {WrapUpKey} after session {SessionId}; it closes in {Duration} or at the next "
            + "connect")]
    private partial void LogWrapUpOpened(string wrapUpKey, string sessionId, TimeSpan duration);

    [LoggerMessage(EventId = 6, EventName = nameof(LogWrapUpClosed), Level = LogLevel.Debug,
        Message = "Wrap-up closed for {WrapUpKey} after session {SessionId}, {Duration} measured")]
    private partial void LogWrapUpClosed(string wrapUpKey, string sessionId, TimeSpan duration);

    [LoggerMessage(EventId = 7, EventName = nameof(LogWrapUpDurationRejected),
        Message = "The wrap-up duration provider answered {Duration} for queue {Queue} on server {ServerId}, which is "
            + "negative or longer than a timer accepts (4294967294 ms); that wrap-up uses SessionOptions.WrapUpDuration. "
            + "Reported at Warning once per server, queue and kind, at Debug after")]
    private partial void LogWrapUpDurationRejected(LogLevel level, string serverId, string queue, TimeSpan duration);

    [LoggerMessage(EventId = 8, EventName = nameof(LogWrapUpDurationProviderFailed),
        Message = "The wrap-up duration provider threw for queue {Queue} on server {ServerId}; that wrap-up uses "
            + "SessionOptions.WrapUpDuration. Reported at Warning once per server, queue and kind, at Debug after")]
    private partial void LogWrapUpDurationProviderFailed(LogLevel level, Exception exception, string serverId,
        string queue);

    [LoggerMessage(EventId = 9, EventName = nameof(LogWrapUpDurationProviderSlow),
        Message = "The wrap-up duration provider took {ElapsedMs} ms to answer for queue {Queue} on server {ServerId}. "
            + "It runs on that server's event stream, which waits for it: it must answer from memory, with no I/O. "
            + "Reported at Warning once per server, queue and kind, at Debug after")]
    private partial void LogWrapUpDurationProviderSlow(LogLevel level, string serverId, string queue, double elapsedMs);

    [LoggerMessage(EventId = 10, EventName = nameof(LogWrapUpDurationExceedsRetention),
        Message = "The wrap-up duration provider answered {Duration} for queue {Queue} on server {ServerId}, longer than "
            + "SessionOptions.CompletedRetention ({Retention}); it is used, but a subscriber that looks the session up "
            + "by id when the wrap-up's event arrives may no longer find it. Reported at Warning once per server, queue "
            + "and kind, at Debug after")]
    private partial void LogWrapUpDurationExceedsRetention(LogLevel level, string serverId, string queue,
        TimeSpan duration, TimeSpan retention);

    [LoggerMessage(EventId = 11, EventName = nameof(LogWrapUpDurationWarnSetFull), Level = LogLevel.Warning,
        Message = "The record of wrap-up duration provider cases already reported holds {Capacity} entries and is full; "
            + "new cases are logged at Debug from now on")]
    private partial void LogWrapUpDurationWarnSetFull(int capacity);

    /// <summary>The wrap-up key of an agent known by name: global, since a name names one person on every server.</summary>
    private static string AgentWrapUpKey(string agentId) => "agent\n" + agentId;

    /// <summary>
    /// The wrap-up key of a member interface, scoped to the server that reported it: interfaces are per PBX, so the
    /// same interface on two servers is two members.
    /// </summary>
    private static string MemberWrapUpKey(string serverId, string memberInterface) =>
        "member\n" + serverId + "\n" + memberInterface;

    /// <summary>
    /// Counts a leg of <paramref name="session"/> that an agent or member took as connected under its key. Called under
    /// the session's lock, after <see cref="CloseWrapUpAtConnect"/>; a leg of a call that has already ended is not
    /// counted, since nothing would end it again.
    /// </summary>
    private void CountConnectedLeg(CallSession session, WrapUpLeg leg)
    {
        if (HasEnded(session))
            return;

        (session.ConnectedAgentLegs ??= []).Add(leg);
        _connectedAgentLegs.AddOrUpdate(leg.Key, 1, static (_, count) => count + 1);
    }

    /// <summary>Takes one leg off its key's connected count and returns how many the key still has connected.</summary>
    private int UncountConnectedLeg(string key)
    {
        while (true)
        {
            if (!_connectedAgentLegs.TryGetValue(key, out var count))
                return 0;

            if (count <= 1)
            {
                if (_connectedAgentLegs.TryRemove(new KeyValuePair<string, int>(key, count)))
                    return 0;
            }
            else if (_connectedAgentLegs.TryUpdate(key, count - 1, count))
            {
                return count - 1;
            }
        }
    }

    /// <summary>
    /// The member's <c>AgentComplete</c> (app_queue's report that the member left the caller): the member's leg ends
    /// here, and, when that leaves the member no other connected leg, its wrap-up opens. It is acted on whether or not
    /// the manager saw the caller join a queue: the queue is the one the frame names, else the one the call last
    /// joined, and with neither nothing is done here (a call outside any queue opens its agent's wrap-up when it ends,
    /// <see cref="EndConnectedLegsAtCallEnd"/>). The call is found by the caller's own channel, which app_queue reports
    /// before the caller hangs up, and by the <c>Linkedid</c> otherwise. The leg is the one counted for this member
    /// interface on <paramref name="serverId"/>, the server that delivered the frame, whichever connect counted it, so
    /// the call's end finds nothing left of it; the key is the member this frame names, never the member the call
    /// recorded first.
    /// <para>
    /// With an <see cref="IWrapUpDurationProvider"/>, the open runs in two steps under the call's lock with the
    /// provider asked between them, holding none of the manager's locks: the first step ends the leg and reads the
    /// open instant, so a slow provider cannot move it; the second takes the open decision with the resolved length.
    /// </para>
    /// </summary>
    private void OnQueueCallerCompleted(QueueCallerCompletion completion, string serverId)
    {
        if (completion.MemberInterface is not { } memberInterface)
            return;

        CallSession? session = null;
        if (completion.CallerUniqueId is { } callerUniqueId)
            _byChannelId.TryGetValue(callerUniqueId, out session);
        if (session is null && completion.LinkedId is { } linkedId)
            _byLinkedId.TryGetValue(linkedId, out session);
        if (session is null)
            return;

        Step(HandlerStep.AgentCompleteFound, serverId, completion.CallerUniqueId ?? completion.LinkedId ?? string.Empty);

        PendingWrapUp pending;
        lock (session.SyncRoot)
        {
            var queue = string.IsNullOrEmpty(completion.Queue) ? session.QueueName : completion.Queue;
            if (queue is null || HasEnded(session))
                return;

            // The member's leg on this server: a queue leg first, else the leg an agent's connect counted on a call
            // the manager saw join no queue (a missed join), which this report ends instead of the call's end.
            var legs = session.ConnectedAgentLegs;
            var index = FindMemberLeg(legs, serverId, memberInterface, opensAtCallEnd: false);
            if (index < 0)
                index = FindMemberLeg(legs, serverId, memberInterface, opensAtCallEnd: true);

            string key;
            string? agentId;
            if (index >= 0)
            {
                var leg = legs![index];
                legs.RemoveAt(index);
                key = leg.Key;
                agentId = leg.AgentId;
                UncountConnectedLeg(key);
            }
            else
            {
                // A leg whose connect the manager did not see: it still ends here, and counts nothing.
                key = MemberWrapUpKey(serverId, memberInterface);
                agentId = null;
            }

            pending = new PendingWrapUp(key, session.SessionId, session.ServerId, agentId, queue,
                _timeProvider.GetUtcNow());

            if (_wrapUpDurations is null)
            {
                OpenWrapUpIfFree(pending, ConfiguredWrapUpDuration());
                return;
            }
        }

        var duration = ResolveWrapUpDuration(_wrapUpDurations, serverId, pending.QueueName);

        lock (session.SyncRoot)
            OpenWrapUpIfFree(pending, duration);
    }

    private static int FindMemberLeg(List<WrapUpLeg>? legs, string serverId, string memberInterface,
        bool opensAtCallEnd) =>
        legs?.FindIndex(l => l.OpensAtCallEnd == opensAtCallEnd
            && string.Equals(l.ServerId, serverId, StringComparison.Ordinal)
            && string.Equals(l.Interface, memberInterface, StringComparison.Ordinal)) ?? -1;

    /// <summary>
    /// Opens <paramref name="pending"/>'s wrap-up when its key has no connected leg left: with app_queue's
    /// <c>ringinuse</c> a member can finish one call while another is up, and a connect handled while the provider was
    /// answering counts too. Called under the call's lock.
    /// </summary>
    private void OpenWrapUpIfFree(PendingWrapUp pending, TimeSpan duration)
    {
        if (_connectedAgentLegs.GetValueOrDefault(pending.Key) == 0)
        {
            OpenWrapUp(pending.Key, pending.SessionId, pending.ServerId, pending.AgentId, pending.QueueName,
                pending.OpenedAt, duration);
        }
    }

    /// <summary>
    /// The call's ending: every leg still counted as connected ends with it. A leg the agent's connect counted for a
    /// call the manager saw join no queue opens its agent's wrap-up here, after the call's
    /// <see cref="CallEndedEvent"/>, when it leaves the agent no other connected leg, and only when no
    /// <c>AgentComplete</c> naming a queue was reported for it: that report ends the leg and takes it off the call
    /// (<see cref="OnQueueCallerCompleted"/>), so it is not here. Such a wrap-up runs
    /// <see cref="SessionOptions.WrapUpDuration"/> and carries the call's queue; no wrap-up duration provider is asked
    /// for it. A queue leg still counted here opens nothing. Nothing opens for an ending a state reload reconciled:
    /// the SDK did not observe it, so no wrap-up it could measure began. Called under the session's lock, once per
    /// call.
    /// </summary>
    private void EndConnectedLegsAtCallEnd(CallSession session)
    {
        if (session.ConnectedAgentLegs is not { Count: > 0 } legs)
            return;

        session.ConnectedAgentLegs = null;
        var endedByReload = session.Metadata.TryGetValue(EndingProvenanceKey, out var cause)
            && string.Equals(cause, EndingProvenanceReload, StringComparison.Ordinal);

        foreach (var leg in legs)
        {
            var stillConnected = UncountConnectedLeg(leg.Key);
            if (leg.OpensAtCallEnd && !endedByReload && stillConnected == 0)
                OpenWrapUp(leg.Key, session.SessionId, session.ServerId, leg.AgentId, session.QueueName,
                    _timeProvider.GetUtcNow(), ConfiguredWrapUpDuration());
        }
    }

    /// <summary>
    /// <see cref="SessionOptions.WrapUpDuration"/> as a timer accepts it. Start-up validation rejects a duration above
    /// the largest due time the system timer accepts; one that got past it (options built by hand) is clamped, so a
    /// timer is never refused on a call's path.
    /// </summary>
    private TimeSpan ConfiguredWrapUpDuration()
    {
        var duration = _options.WrapUpDuration;
        if (duration > WrapUpDurationRangeAttribute.Max)
            return WrapUpDurationRangeAttribute.Max;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>
    /// Opens the wrap-up of <paramref name="key"/>, which began at <paramref name="openedAt"/> and lasts
    /// <paramref name="duration"/>, a length between zero and the largest due time the system timer accepts. A
    /// wrap-up already open under the key is closed first, with the time it measured: the same agent's or member's
    /// next leg has ended. Its timer is due what is left of <paramref name="duration"/> now; a duration of zero, or
    /// one already over, closes the new wrap-up in the same instant, with no timer.
    /// </summary>
    private void OpenWrapUp(string key, string sessionId, string serverId, string? agentId, string? queueName,
        DateTimeOffset openedAt, TimeSpan duration)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = new WrapUpEntry(key, sessionId, serverId, agentId, queueName, openedAt, duration);
        LogWrapUpOpened(key, sessionId, duration);

        var left = duration - (now - openedAt);
        if (left <= TimeSpan.Zero)
        {
            if (_openWrapUps.TryRemove(key, out var replaced))
                CloseWrapUp(replaced, now);
            RaiseWrapUp(entry, now);
            return;
        }

        while (!_openWrapUps.TryAdd(key, entry))
        {
            if (_openWrapUps.TryRemove(key, out var replaced))
                CloseWrapUp(replaced, now);
        }

        _wrapUpTimers[entry] = 0;
        entry.Timer = _timeProvider.CreateTimer(
            state => OnWrapUpElapsed((WrapUpEntry)state!), entry, left, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// A wrap-up's duration has passed. Its callback closes it only if that very entry is still open: one closed or
    /// replaced meanwhile has been reported by whoever took it out, so this callback raises nothing.
    /// </summary>
    private void OnWrapUpElapsed(WrapUpEntry entry)
    {
        if (_openWrapUps.TryRemove(new KeyValuePair<string, WrapUpEntry>(entry.Key, entry)))
            RaiseWrapUp(entry, _timeProvider.GetUtcNow());

        ReleaseWrapUpTimer(entry);
    }

    /// <summary>
    /// The same agent's or member's next call connected: its open wrap-up, if any, closes now with the time measured so
    /// far. Called before that call's <see cref="CallConnectedEvent"/> is raised.
    /// </summary>
    private void CloseWrapUpAtConnect(string key)
    {
        if (_openWrapUps.TryRemove(key, out var open))
            CloseWrapUp(open, _timeProvider.GetUtcNow());
    }

    /// <summary>Reports a wrap-up its caller has just taken out of the open set, and lets its timer go.</summary>
    private void CloseWrapUp(WrapUpEntry entry, DateTimeOffset now)
    {
        RaiseWrapUp(entry, now);
        ReleaseWrapUpTimer(entry);
    }

    /// <summary>
    /// Raises the one <see cref="CallWrapUpEvent"/> of a closed wrap-up, stamped with the close instant and carrying
    /// the time from its opening to that instant. A wrap-up never outlasts its duration: a timer that fires late, or a
    /// connect handled after the duration passed but before the timer's callback ran, closes it at the instant it was
    /// due. Nothing is raised once disposal has begun.
    /// </summary>
    private void RaiseWrapUp(WrapUpEntry entry, DateTimeOffset now)
    {
        var due = entry.OpenedAt + entry.Duration;
        var closedAt = now < due ? now : due;
        if (closedAt < entry.OpenedAt)
            closedAt = entry.OpenedAt;

        var measured = closedAt - entry.OpenedAt;
        lock (_wrapUpRaise)
        {
            if (Volatile.Read(ref _wrapUpsDisposed))
                return;

            LogWrapUpClosed(entry.Key, entry.SessionId, measured);
            Publish(new CallWrapUpEvent(entry.SessionId, entry.ServerId, closedAt, entry.AgentId,
                entry.QueueName, measured));
        }
    }

    private void ReleaseWrapUpTimer(WrapUpEntry entry)
    {
        entry.Timer?.Dispose();
        _wrapUpTimers.TryRemove(entry, out _);
    }

    /// <summary>
    /// Stops every wrap-up for disposal: no close raises from here on, every open wrap-up is dropped unreported, and
    /// every timer is disposed and awaited, which waits for a callback already running on a pool thread. Returns once
    /// no close can still be raising, so the event stream can complete.
    /// </summary>
    private async ValueTask StopWrapUpsAsync()
    {
        Volatile.Write(ref _wrapUpsDisposed, true);

        foreach (var key in _openWrapUps.Keys.ToArray())
            _openWrapUps.TryRemove(key, out _);

        foreach (var entry in _wrapUpTimers.Keys.ToArray())
        {
            if (entry.Timer is { } timer)
                await timer.DisposeAsync().ConfigureAwait(false);
            _wrapUpTimers.TryRemove(entry, out _);
        }

        // A fence, taken only after the timers' disposal has been awaited, so it never blocks this thread on a
        // subscriber a timer's close is still running: a close raised from an AMI thread that checked the flag before
        // it was set finishes before this returns.
        _wrapUpRaise.Enter();
        _wrapUpRaise.Exit();
    }

    /// <summary>
    /// The length of the wrap-up an <c>AgentComplete</c> delivered by <paramref name="serverId"/> for
    /// <paramref name="queue"/> opens: the provider's answer when it is one a timer accepts, and
    /// <see cref="SessionOptions.WrapUpDuration"/> when it answers <see langword="null"/>, an unusable value or throws.
    /// Asked exactly once, holding none of the manager's locks, and timed on the manager's clock. Nothing it throws
    /// leaves here but <see cref="OutOfMemoryException"/>.
    /// </summary>
    private TimeSpan ResolveWrapUpDuration(IWrapUpDurationProvider provider, string serverId, string queue)
    {
        var started = _timeProvider.GetTimestamp();
        TimeSpan? answer = null;
        Exception? failure = null;
        try
        {
            answer = provider.GetWrapUpDuration(serverId, queue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
        }

        var elapsed = _timeProvider.GetElapsedTime(started);
        var duration = ConfiguredWrapUpDuration();
        if (failure is not null)
        {
            var level = WrapUpDurationLogLevel(serverId, queue, WrapUpDurationAnomaly.Failed);
            LogWrapUpDurationProviderFailed(level, failure, serverId, queue);
        }
        else if (answer is { } value)
        {
            if (value < TimeSpan.Zero || value > WrapUpDurationRangeAttribute.Max)
            {
                var level = WrapUpDurationLogLevel(serverId, queue, WrapUpDurationAnomaly.Rejected);
                LogWrapUpDurationRejected(level, serverId, queue, value);
            }
            else
            {
                duration = value;
                if (value > _options.CompletedRetention)
                {
                    var level = WrapUpDurationLogLevel(serverId, queue, WrapUpDurationAnomaly.ExceedsRetention);
                    LogWrapUpDurationExceedsRetention(level, serverId, queue, value, _options.CompletedRetention);
                }
            }
        }

        if (elapsed > WrapUpDurationProviderSlowBound)
        {
            var level = WrapUpDurationLogLevel(serverId, queue, WrapUpDurationAnomaly.Slow);
            var elapsedMs = elapsed.TotalMilliseconds;
            LogWrapUpDurationProviderSlow(level, serverId, queue, elapsedMs);
        }

        return duration;
    }

    /// <summary>
    /// Warning the first time a (server, queue, kind) case is reported, Debug after. A case the full record cannot
    /// take logs at Debug, and the first one says, once per manager, that the record is full.
    /// </summary>
    private LogLevel WrapUpDurationLogLevel(string serverId, string queue, WrapUpDurationAnomaly kind)
    {
        var key = (serverId, queue, kind);
        if (_warnedWrapUpDurations.ContainsKey(key))
            return LogLevel.Debug;

        if (Interlocked.Increment(ref _warnedWrapUpDurationCount) > WrapUpDurationWarnSetCapacity)
        {
            Interlocked.Decrement(ref _warnedWrapUpDurationCount);
            if (Interlocked.Exchange(ref _wrapUpDurationWarnSetFullReported, 1) == 0)
                LogWrapUpDurationWarnSetFull(WrapUpDurationWarnSetCapacity);
            return LogLevel.Debug;
        }

        if (_warnedWrapUpDurations.TryAdd(key, 0))
            return LogLevel.Warning;

        // Another thread recorded this case first and logged it at Warning.
        Interlocked.Decrement(ref _warnedWrapUpDurationCount);
        return LogLevel.Debug;
    }

    /// <summary>What a wrap-up duration provider did that the manager reports.</summary>
    private enum WrapUpDurationAnomaly
    {
        Rejected,
        Failed,
        Slow,
        ExceedsRetention,
    }

    /// <summary>
    /// A wrap-up an <c>AgentComplete</c> is about to open: its key, the strings its event carries, and the instant it
    /// opens, read before any wrap-up duration provider is asked.
    /// </summary>
    private sealed record PendingWrapUp(string Key, string SessionId, string ServerId, string? AgentId,
        string QueueName, DateTimeOffset OpenedAt);

    /// <summary>
    /// One open wrap-up. It holds the strings its event carries, the instant it opened, its duration and its timer,
    /// never the call: the call is released on its own schedule.
    /// </summary>
    private sealed class WrapUpEntry(string key, string sessionId, string serverId, string? agentId, string? queueName,
        DateTimeOffset openedAt, TimeSpan duration)
    {
        public string Key { get; } = key;
        public string SessionId { get; } = sessionId;
        public string ServerId { get; } = serverId;
        public string? AgentId { get; } = agentId;
        public string? QueueName { get; } = queueName;
        public DateTimeOffset OpenedAt { get; } = openedAt;
        public TimeSpan Duration { get; } = duration;
        public ITimer? Timer { get; set; }
    }
}
