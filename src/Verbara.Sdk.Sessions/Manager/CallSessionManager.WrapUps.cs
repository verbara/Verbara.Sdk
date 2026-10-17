using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Sessions.Internal;

namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// One leg of a call that an agent or a queue member took, counted as connected for that agent's or member's
/// wrap-up until its end is handled. <paramref name="Key"/> is the wrap-up key the leg counts under;
/// <paramref name="OpensAtCallEnd"/> is set for a call outside any queue, whose leg ends, and whose wrap-up opens,
/// when the call ends; a queue leg ends at the member's <c>AgentComplete</c>.
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

    [LoggerMessage(EventId = 5, EventName = nameof(LogWrapUpOpened), Level = LogLevel.Debug,
        Message = "Wrap-up opened for {WrapUpKey} after session {SessionId}; it closes in {Duration} or at the next "
            + "connect")]
    private partial void LogWrapUpOpened(string wrapUpKey, string sessionId, TimeSpan duration);

    [LoggerMessage(EventId = 6, EventName = nameof(LogWrapUpClosed), Level = LogLevel.Debug,
        Message = "Wrap-up closed for {WrapUpKey} after session {SessionId}, {Duration} measured")]
    private partial void LogWrapUpClosed(string wrapUpKey, string sessionId, TimeSpan duration);

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
    /// here, and, when that leaves the member no other connected leg, its wrap-up opens. Only a queue call is acted on;
    /// a call outside any queue opens its agent's wrap-up when it ends (<see cref="EndConnectedLegsAtCallEnd"/>). The
    /// call is found by the caller's own channel, which app_queue reports before the caller hangs up, and by the
    /// <c>Linkedid</c> otherwise. The key is the member this frame names, never the member the call recorded first.
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

        lock (session.SyncRoot)
        {
            if (session.QueueName is null || HasEnded(session))
                return;

            var legs = session.ConnectedAgentLegs;
            var index = legs?.FindIndex(l => !l.OpensAtCallEnd
                && string.Equals(l.ServerId, serverId, StringComparison.Ordinal)
                && string.Equals(l.Interface, memberInterface, StringComparison.Ordinal)) ?? -1;

            string key;
            string? agentId;
            int stillConnected;
            if (index >= 0)
            {
                var leg = legs![index];
                legs.RemoveAt(index);
                key = leg.Key;
                agentId = leg.AgentId;
                stillConnected = UncountConnectedLeg(key);
            }
            else
            {
                // A leg whose connect the manager did not see: it still ends here, and counts nothing.
                key = MemberWrapUpKey(serverId, memberInterface);
                agentId = null;
                stillConnected = _connectedAgentLegs.GetValueOrDefault(key);
            }

            if (stillConnected == 0)
                OpenWrapUp(key, session.SessionId, session.ServerId, agentId, session.QueueName);
        }
    }

    /// <summary>
    /// The call's ending: every leg still counted as connected ends with it. A leg of a call outside any queue opens
    /// its agent's wrap-up here, after the call's <see cref="CallEndedEvent"/>, when it leaves the agent no other
    /// connected leg. A queue leg opened its member's wrap-up at that member's <c>AgentComplete</c>, which app_queue
    /// reports before the caller hangs up, so one still counted here opens nothing. Nothing opens for an ending a
    /// state reload reconciled: the SDK did not observe it, so no wrap-up it could measure began. Called under the
    /// session's lock, once per call.
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
                OpenWrapUp(leg.Key, session.SessionId, session.ServerId, leg.AgentId, session.QueueName);
        }
    }

    /// <summary>
    /// Opens the wrap-up of <paramref name="key"/> now. A wrap-up already open under the key is closed first, with the
    /// time it measured: the same agent's or member's next leg has ended. A duration of zero closes the new wrap-up in
    /// the same instant, with no timer.
    /// </summary>
    private void OpenWrapUp(string key, string sessionId, string serverId, string? agentId, string? queueName)
    {
        var now = _timeProvider.GetUtcNow();
        var duration = _options.WrapUpDuration;
        // Start-up validation rejects a duration above the largest due time the system timer accepts; one that got
        // past it (options built by hand) is clamped, so a timer is never refused on a call's path.
        if (duration > WrapUpDurationRangeAttribute.Max)
            duration = WrapUpDurationRangeAttribute.Max;
        else if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var entry = new WrapUpEntry(key, sessionId, serverId, agentId, queueName, now, duration);
        LogWrapUpOpened(key, sessionId, duration);

        if (duration == TimeSpan.Zero)
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
            state => OnWrapUpElapsed((WrapUpEntry)state!), entry, duration, Timeout.InfiniteTimeSpan);
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
            _events.OnNext(new CallWrapUpEvent(entry.SessionId, entry.ServerId, closedAt, entry.AgentId,
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

        // A close raised from an AMI thread that checked the flag before it was set finishes before this returns.
        lock (_wrapUpRaise)
        {
        }
    }

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
