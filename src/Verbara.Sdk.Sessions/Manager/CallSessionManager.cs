using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reactive.Subjects;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Bridges;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Sessions.Manager;

public sealed partial class CallSessionManager : ICallSessionManager
{
    private readonly ConcurrentDictionary<string, CallSession> _sessions = new();
    private readonly ConcurrentDictionary<string, CallSession> _byLinkedId = new();
    private readonly ConcurrentDictionary<string, CallSession> _byChannelId = new();
    private readonly ConcurrentDictionary<string, string> _bridgeToSession = new();
    private readonly ConcurrentQueue<ReleaseEntry> _completedOrder = new();

    /// <summary>
    /// Serializes the release walk. The walk judges an entry after taking it off the queue, which is
    /// only sound while no other walk can take an entry between its look at the head and its dequeue;
    /// every arrival and every ending runs a walk, and with several servers attached they run on
    /// different threads at once.
    /// </summary>
    private readonly Lock _releaseLock = new();
    private readonly ConcurrentDictionary<string, ServerSubscriptions> _serverSubs = new();
    private readonly Subject<SessionDomainEvent> _events = new();
    private readonly SessionCorrelator _correlator;
    private readonly SessionOptions _options;
    private readonly SessionStoreBase _store;
    private readonly ILogger<CallSessionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Meter _residencyMeter;
    private CancellationToken _shutdownToken;

    public CallSessionManager(
        IOptions<SessionOptions> options,
        ILogger<CallSessionManager> logger,
        SessionStoreBase store)
        : this(options, logger, store, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a manager whose release cutoff — the instant an ended session must have
    /// completed before to be past <see cref="SessionOptions.CompletedRetention"/> — is read from
    /// <paramref name="timeProvider"/>, so a test can move past the retention period with a fake
    /// clock instead of sitting it out. The same clock gives the two instants of a queue visit: its
    /// start, which stamps <see cref="CallQueuedEvent"/>, and the connect, which stamps
    /// <see cref="CallConnectedEvent"/>; the queue wait-time histogram records the difference, so a
    /// test fixes a visit's length exactly. The start is the instant the manager handles the join or,
    /// for a caller a reload's queue snapshot reports waiting, that instant minus the wait Asterisk
    /// reports for it: a duration Asterisk measured, subtracted from this clock, so no two clocks are
    /// compared. Nothing else reads it: the session's own timestamps
    /// (<see cref="CallSession.CreatedAt"/>, <see cref="CallSession.ConnectedAt"/>) and its audit
    /// trail still come from the wall clock.
    /// </summary>
    internal CallSessionManager(
        IOptions<SessionOptions> options,
        ILogger<CallSessionManager> logger,
        SessionStoreBase store,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _logger = logger;
        _store = store;
        _timeProvider = timeProvider;
        _correlator = new SessionCorrelator(_options);
        _residencyMeter = SessionMetrics.CreateResidencyMeter(
            active: () => CountHeld(ended: false),
            retained: () => CountHeld(ended: true));
    }

    /// <summary>
    /// The meter this manager publishes its resident counts on (<c>sessions.active</c>,
    /// <c>sessions.retained</c>), disposed with the manager. Every manager has its own under the
    /// <c>Verbara.Sdk.Sessions</c> name; a test picks this manager's gauges out by it.
    /// </summary>
    internal Meter ResidencyMeter => _residencyMeter;

    /// <summary>
    /// Sets the token <em>every</em> persistence call runs under, not only the ones at shutdown.
    /// With the SDK's hosted service the token comes from a source that service owns, and the stop
    /// phase is what cancels it: the host withdrawing its graceful shutdown, or that service's own
    /// teardown. An aborted start does not cancel it.
    /// </summary>
    internal void SetShutdownToken(CancellationToken token) => _shutdownToken = token;

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist session {SessionId}")]
    private partial void LogPersistError(Exception ex, string sessionId);

    /// <summary>
    /// Saves <paramref name="session"/> to the store — only while this manager still holds it.
    /// <para>
    /// A session it has released is never handed to the store again. A leg can outlive the call it
    /// joined (a late leg reusing the call's <c>linkedid</c>, or one a reload admitted), and its
    /// changes still reach this method; a save then would put back into the default store a call the
    /// SDK has let go of, and hand a durable store a call the SDK no longer tracks. The check runs
    /// again once the save is done, because with several servers attached a walk on another thread
    /// can release the session between the first check and the store's write: if it did, the default
    /// store is told again, after the write, so the write cannot outlast the release
    /// (<c>ADR-0063</c>, D5).
    /// </para>
    /// </summary>
    private async Task PersistAsync(CallSession session)
    {
        if (!IsHeld(session))
            return;

        try
        {
            await _store.SaveAsync(session, _shutdownToken);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            // The persistence token was cancelled — with the SDK's hosted service, by the host
            // withdrawing its graceful shutdown or by that service's teardown. The save was cut
            // short on purpose, so it is not a persistence failure and is not logged. A cancellation
            // while the token is still live, such as a store-side timeout, is still logged below.
        }
        catch (Exception ex)
        {
            LogPersistError(ex, session.SessionId);
        }

        if (!IsHeld(session))
            _store.OnReleasedByManager(session);
    }

    public IObservable<SessionDomainEvent> Events => _events;

    public IEnumerable<CallSession> ActiveSessions => _sessions.Values.Where(s => !HasEnded(s));

    public CallSession? GetById(string sessionId) => _sessions.GetValueOrDefault(sessionId);
    public CallSession? GetByLinkedId(string linkedId) => _byLinkedId.GetValueOrDefault(linkedId);
    public CallSession? GetByChannelId(string uniqueId) => _byChannelId.GetValueOrDefault(uniqueId);

    public CallSession? GetByBridgeId(string bridgeId) =>
        _bridgeToSession.TryGetValue(bridgeId, out var sessionId)
            ? _sessions.GetValueOrDefault(sessionId)
            : null;

    public IEnumerable<CallSession> GetRecentCompleted(int count = 100) =>
        _sessions.Values
            .Where(HasEnded)
            .OrderByDescending(s => s.CompletedAt)
            .Take(count);

    /// <summary>
    /// Whether <paramref name="session"/> is in a terminal state. The one split between the calls this
    /// manager holds in progress and the ended ones it still holds: <see cref="ActiveSessions"/>,
    /// <see cref="GetRecentCompleted"/> and the resident-count gauges all read it.
    /// </summary>
    private static bool HasEnded(CallSession session) =>
        session.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut;

    /// <summary>
    /// How many sessions this manager holds that have ended (<paramref name="ended"/>) or have not —
    /// the values of the <c>sessions.retained</c> and <c>sessions.active</c> gauges.
    /// <para>
    /// The ended count is of the sessions held, not of the release queue's entries. The queue holds
    /// the ended calls whose release is pending, and some ended calls are held with no entry: one the
    /// timeout sweep ended while its legs were still up, and whose legs are never seen to leave; one
    /// whose entry carried no completion time and was dropped with the call kept; one registered
    /// already ended. A count of the queue would report those released while the process keeps them
    /// — a bound that looks as if it works while the memory stays held, which is what these gauges
    /// exist to show (<c>ADR-0063</c>).
    /// </para>
    /// <para>
    /// One pass over the held sessions, which takes no lock and copies nothing (unlike
    /// <c>_sessions.Values</c>). It runs only when a listener collects the gauges, never on a call's
    /// path.
    /// </para>
    /// </summary>
    private long CountHeld(bool ended)
    {
        var count = 0L;
        foreach (var held in _sessions)
        {
            if (HasEnded(held.Value) == ended)
                count++;
        }

        return count;
    }

    public void AttachToServer(VerbaraServer server, string serverId)
    {
        // Create typed delegates so we can -= unsubscribe later
        Action<AsteriskChannel> onAdded = ch => OnChannelAdded(ch, serverId);
        Action<AsteriskChannel> onRemoved = OnChannelRemoved;
        Action<AsteriskChannel> onStateChanged = OnChannelStateChanged;
        Action<AsteriskChannel> onDialBegin = OnChannelDialBegin;
        Action<AsteriskChannel> onDialEnd = OnChannelDialEnd;
        Action<AsteriskChannel> onHeld = OnChannelHeld;
        Action<AsteriskChannel> onUnheld = OnChannelUnheld;
        Action<AsteriskBridge, string> onBridgeEntered = OnBridgeChannelEntered;
        Action<AsteriskBridge> onBridgeDestroyed = OnBridgeDestroyed;
        Action<BridgeTransferInfo> onTransfer = OnTransfer;
        Action<string, AsteriskQueueEntry> onCallerJoined = OnQueueCallerJoined;
        Action<string, AsteriskQueueEntry> onCallerLeft = OnQueueCallerLeft;
        Action<string, string?, string?> onAgentConnected = OnAgentConnected;
        Action<string?, string?, string?> onQueueCallerConnected = OnQueueCallerConnected;

        server.Channels.ChannelAdded += onAdded;
        server.Channels.ChannelRemoved += onRemoved;
        server.Channels.ChannelStateChanged += onStateChanged;
        server.Channels.ChannelDialBegin += onDialBegin;
        server.Channels.ChannelDialEnd += onDialEnd;
        server.Channels.ChannelHeld += onHeld;
        server.Channels.ChannelUnheld += onUnheld;
        server.Bridges.ChannelEntered += onBridgeEntered;
        server.Bridges.BridgeDestroyed += onBridgeDestroyed;
        server.Bridges.TransferOccurred += onTransfer;
        server.Queues.CallerJoined += onCallerJoined;
        server.Queues.CallerLeft += onCallerLeft;
        server.Agents.AgentConnected += onAgentConnected;
        server.Agents.QueueCallerConnected += onQueueCallerConnected;

        _serverSubs[serverId] = new ServerSubscriptions(server,
            onAdded, onRemoved, onStateChanged, onDialBegin, onDialEnd,
            onHeld, onUnheld, onBridgeEntered, onBridgeDestroyed, onTransfer, onCallerJoined,
            onCallerLeft, onAgentConnected, onQueueCallerConnected);
    }

    public void DetachFromServer(string serverId)
    {
        if (_serverSubs.TryRemove(serverId, out var subs))
            subs.Detach();
    }

    // --- Event Handlers ---

    /// <summary>
    /// The metadata key under which the SDK records that it did <em>not</em> observe a session
    /// start. It is the opening counterpart of <see cref="EndingProvenanceKey"/>: <c>origin</c> says
    /// how the SDK came to know about the call, <c>cause</c> how it came to know it ended.
    /// </summary>
    private const string OriginKey = "origin";

    /// <summary>
    /// The value <see cref="OriginKey"/> carries when the session was opened from a channel a
    /// <c>Status</c> snapshot reported — a first load or a post-reconnect reload. A consumer reads
    /// it as positive evidence that the call was already in progress when the SDK learned of it, so
    /// <see cref="CallSession.CreatedAt"/> is when the session record was opened rather than when
    /// the call began, and any state on it was reported rather than watched (<c>ADR-0062</c>,
    /// design D5).
    /// </summary>
    private const string OriginReload = "reload";

    /// <summary>
    /// The state a session opens in when the channel it is opened from came out of a <c>Status</c>
    /// snapshot, or <c>null</c> when the snapshot's state says nothing worth carrying and the
    /// session opens in <see cref="CallSessionState.Created"/> exactly as it always has.
    /// <para>
    /// The mapping is the one <see cref="OnChannelStateChanged"/> already performs for a live
    /// channel — <c>Ring</c>/<c>Ringing</c> to <see cref="CallSessionState.Ringing"/>, <c>Up</c> to
    /// <see cref="CallSessionState.Connected"/> — deliberately, so a reloaded call is described in
    /// the same vocabulary as a call the SDK watched. The difference is only <em>when</em> it is
    /// applied: a transition cannot reach either state from <c>Created</c>, so for a reloaded
    /// channel it has to be applied at construction.
    /// </para>
    /// <para>
    /// Every other state — including <see cref="ChannelState.Unknown"/>, which is what a frame
    /// carrying no state header at all becomes — maps to <c>null</c>. <c>Down</c> and <c>Reserved</c>
    /// describe a channel that is not carrying a call yet, and <c>Dialing</c>, <c>OffHook</c>,
    /// <c>Busy</c>, <c>DialingOffHook</c> and <c>PreRing</c> have no session state this SDK has ever
    /// derived from a channel state; inventing one here would be this method asserting more than the
    /// snapshot said.
    /// </para>
    /// </summary>
    private static CallSessionState? ReportedSessionState(ChannelState channelState) => channelState switch
    {
        ChannelState.Ringing or ChannelState.Ring => CallSessionState.Ringing,
        ChannelState.Up => CallSessionState.Connected,
        _ => null,
    };

    private void OnChannelAdded(AsteriskChannel channel, string serverId)
    {
        // Release rides arrivals as well as endings, so a process that keeps accepting calls but has
        // stopped completing them still lets go of what it holds past retention. It runs before the
        // leg is correlated: a leg carrying the linkedid of an ended call past retention then finds
        // that call released and opens its own, instead of joining a call this same evaluation lets
        // go of. No timer is involved, so an idle process releases nothing (ADR-0063, D4).
        EvictStaleCompleted();

        var linkedId = channel.LinkedId;
        if (string.IsNullOrEmpty(linkedId)) linkedId = channel.UniqueId;

        if (_byLinkedId.TryGetValue(linkedId, out var existing))
        {
            // Add participant to existing session
            lock (existing.SyncRoot)
            {
                var role = SessionCorrelator.InferRole(channel.Name, existing.Participants.Count);
                existing.AddParticipant(new SessionParticipant
                {
                    UniqueId = channel.UniqueId,
                    Channel = channel.Name,
                    Technology = SessionCorrelator.ExtractTechnology(channel.Name),
                    Role = role,
                    CallerIdNum = channel.CallerIdNum,
                    CallerIdName = channel.CallerIdName,
                    JoinedAt = DateTimeOffset.UtcNow
                });
                existing.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.ParticipantJoined, channel.Name, null, role.ToString()));
            }
            _byChannelId[channel.UniqueId] = existing;
            _ = PersistAsync(existing);
            return;
        }

        // Create new session
        var direction = _correlator.InferDirection(channel.Context, channel.Extension);
        var session = new CallSession(Guid.NewGuid().ToString("N"), linkedId, serverId, direction);

        session.Context = channel.Context;
        session.Extension = channel.Extension;

        // A channel that came out of a Status snapshot — a first load or a post-reconnect reload —
        // is a call the SDK never saw start. The state on it is Asterisk's own account of the call
        // and the only one that will ever arrive: no NewState announcing it is coming, because it
        // already happened. Opening such a call in Created would report a live conversation as one
        // that has not started, and Created past a dialing timeout is exactly what
        // SessionReconciler's orphan branch fails (ADR-0062, design D5).
        var reportedState = channel.AdmittedFromSnapshot ? ReportedSessionState(channel.State) : null;
        if (channel.AdmittedFromSnapshot)
        {
            // Marked whatever state was reported, including the one the snapshot could not
            // determine: what the marker says is that CreatedAt is when the SDK learned of this
            // call and not when the call began, and that every timestamp before it is unobserved.
            // Positive evidence, read the way the ending's "cause" marker is — never inferred from
            // a null ConnectedAt.
            session.SetMetadata(OriginKey, OriginReload);
        }

        if (reportedState is { } live)
            session.OpenInReportedState(live);

        var callerRole = SessionCorrelator.InferRole(channel.Name, 0);
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = channel.UniqueId,
            Channel = channel.Name,
            Technology = SessionCorrelator.ExtractTechnology(channel.Name),
            Role = callerRole,
            CallerIdNum = channel.CallerIdNum,
            CallerIdName = channel.CallerIdName,
            JoinedAt = DateTimeOffset.UtcNow
        });
        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
            CallSessionEventType.Created, channel.Name, null, null));

        // The audit trail says where the state came from, so a reader cannot mistake it for an
        // answer or a ring this SDK watched happen. Its timestamp is when the snapshot was read,
        // which is why the state is not also written into ConnectedAt / RingingAt.
        if (reportedState is { } reported)
        {
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                reported is CallSessionState.Connected
                    ? CallSessionEventType.Connected
                    : CallSessionEventType.Ringing,
                channel.Name, null, OriginReload));
        }

        _sessions[session.SessionId] = session;
        _byLinkedId[linkedId] = session;
        _byChannelId[channel.UniqueId] = session;
        SessionMetrics.SessionsCreated.Add(1);

        _events.OnNext(new CallStartedEvent(session.SessionId, serverId,
            DateTimeOffset.UtcNow, direction, channel.CallerIdNum));

        _ = PersistAsync(session);
    }

    /// <summary>
    /// The metadata key under which the SDK records how a session ended when it did <em>not</em>
    /// observe the ending. Already carries <c>"orphaned"</c> from
    /// <see cref="SessionReconciler.TryMarkOrphaned"/>; <see cref="EndingProvenanceReload"/> joins
    /// it rather than opening a second vocabulary for the same question.
    /// </summary>
    private const string EndingProvenanceKey = "cause";

    /// <summary>
    /// The value <see cref="EndingProvenanceKey"/> carries when a completed state reload proved the
    /// call's channels gone. A consumer reads it as positive evidence that no hangup was observed —
    /// never by noticing that <c>HangupCause</c> is null (<c>ADR-0062</c>, design D3).
    /// </summary>
    private const string EndingProvenanceReload = "reload";

    /// <summary>
    /// Ends the session a departing channel belongs to.
    /// <para>
    /// A removal arrives by one of two routes, and they carry different knowledge.
    /// <c>ChannelManager.OnHangup</c> observed Asterisk's <c>Hangup</c> and put its cause on the
    /// channel. <c>ChannelManager.ReconcileWithSnapshot</c> observed only that a completed reload
    /// no longer lists the channel: no hangup was seen, no cause exists, and
    /// <see cref="AsteriskChannel.HangupCause"/> still holds its non-nullable default
    /// <c>NotDefined</c> — cause zero. Reading that default here would invent a cause, and
    /// <c>NotDefined</c> is not neutral downstream: a classifier that treats anything other than
    /// <c>NormalClearing</c> as abnormal reads every reconnect-lost call as an abnormal hangup and
    /// acts on it. So a reload-produced ending carries no cause at all and is marked instead
    /// (<c>ADR-0062</c>, design D3).
    /// </para>
    /// </summary>
    private void OnChannelRemoved(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        var byReload = channel.RemovedByReload;

        // The one read of channel.HangupCause in this method. Null when nothing was observed —
        // distinct from HangupCause.NotDefined, which is what "observed, and Asterisk said zero"
        // would look like.
        var observedCause = byReload ? (HangupCause?)null : channel.HangupCause;

        lock (session.SyncRoot)
        {
            var participant = session.Participants.FirstOrDefault(p => p.UniqueId == channel.UniqueId);
            if (participant is not null)
            {
                participant.LeftAt = DateTimeOffset.UtcNow;
                participant.HangupCause = observedCause;
            }

            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.ParticipantLeft, channel.Name, null,
                byReload ? EndingProvenanceReload : observedCause.ToString()));

            // Check if all participants have left
            if (session.Participants.All(p => p.LeftAt.HasValue))
            {
                session.HangupCause = observedCause;

                if (byReload)
                {
                    // Marked on the session, which is what a consumer can reach from the SessionId
                    // on CallEndedEvent. The event record itself gains no field: CallEndedEvent is
                    // a positional record, so adding one would move the public API this change
                    // claims it does not touch.
                    session.SetMetadata(EndingProvenanceKey, EndingProvenanceReload);

                    // No cause exists, so the outcome follows from what the session already was: a
                    // call that was up really took place and is over; one that never connected
                    // never became a call. Transferring is deliberately absent — it has no valid
                    // transition to Completed — and falls to the Failed arm below.
                    var reloadTarget = session.State is CallSessionState.Connected
                        or CallSessionState.OnHold or CallSessionState.Conference
                        ? CallSessionState.Completed
                        : CallSessionState.Failed;

                    if (!session.TryTransition(reloadTarget))
                        session.TryTransition(CallSessionState.Failed);

                    LogEndedByReload(session.SessionId, session.LinkedId, session.State);
                }
                else
                {
                    var targetState = channel.HangupCause == HangupCause.NormalClearing
                        ? CallSessionState.Completed
                        : CallSessionState.Failed;

                    // Try the natural progression if needed
                    if (session.State == CallSessionState.Created)
                    {
                        session.TryTransition(CallSessionState.Failed);
                    }
                    else if (!session.TryTransition(targetState))
                    {
                        session.TryTransition(CallSessionState.Failed);
                    }
                }

                OnSessionCompleted(session);
            }
            else
            {
                _ = PersistAsync(session);
            }
        }

        _byChannelId.TryRemove(channel.UniqueId, out _);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} (linked {LinkedId}) ended as {State} because a state reload "
            + "proved its channels gone; no hangup was observed, so no cause is recorded")]
    private partial void LogEndedByReload(string sessionId, string linkedId, CallSessionState state);

    private void OnChannelStateChanged(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            var changed = false;
            switch (channel.State)
            {
                case ChannelState.Ringing or ChannelState.Ring:
                    if (session.TryTransition(CallSessionState.Ringing))
                    {
                        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                            CallSessionEventType.Ringing, channel.Name, null, null));
                        changed = true;
                    }
                    break;

                case ChannelState.Up:
                    if (session.TryTransition(CallSessionState.Connected))
                    {
                        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                            CallSessionEventType.Connected, channel.Name, null, null));
                        changed = true;
                    }
                    break;
            }

            if (changed)
                _ = PersistAsync(session);
        }
    }

    private void OnChannelHeld(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.OnHold))
            {
                session.StartHold();
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Hold, channel.Name, null, channel.HoldMusicClass));
                _events.OnNext(new CallHeldEvent(session.SessionId, session.ServerId, DateTimeOffset.UtcNow));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnChannelUnheld(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Connected))
            {
                session.EndHold();
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Unhold, channel.Name, null, null));
                _events.OnNext(new CallResumedEvent(session.SessionId, session.ServerId, DateTimeOffset.UtcNow));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnBridgeChannelEntered(AsteriskBridge bridge, string uniqueId)
    {
        if (!_byChannelId.TryGetValue(uniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            session.BridgeId = bridge.BridgeUniqueid;
            _bridgeToSession[bridge.BridgeUniqueid] = session.SessionId;

            if (session.TryTransition(CallSessionState.Connected))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Connected, null, null, $"bridge:{bridge.BridgeUniqueid}"));

                // A bridge announces only a call that never joined a queue, every time it connects it,
                // as it always has. A queued call is announced when app_queue reports the connection
                // (OnQueueCallerConnected): a member leg can enter a bridge that app_queue never
                // connects, as a pooled agent that never acknowledges does.
                if (session.QueueName is null)
                    PublishConnected(session, session.AgentId);

                _ = PersistAsync(session);
            }
        }
    }

    private void OnTransfer(BridgeTransferInfo info)
    {
        var session = GetByBridgeId(info.BridgeId);
        if (session is null) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Transferring))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Transfer, null, info.TargetChannel, info.TransferType));
                _events.OnNext(new CallTransferredEvent(session.SessionId, session.ServerId,
                    DateTimeOffset.UtcNow, info.TransferType, info.TargetChannel));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnQueueCallerJoined(string queueName, AsteriskQueueEntry entry)
    {
        var session = FindByChannelName(entry.Channel);
        if (session is null) return;

        DateTimeOffset visitStartedAt;
        lock (session.SyncRoot)
        {
            var now = _timeProvider.GetUtcNow();
            var reportedJoin = ReportedJoin(now, entry);

            // A queue snapshot that finds the caller still waiting in the visit this manager holds open
            // reports that same visit again. Nothing is published and nothing on the session changes: the
            // visit keeps its start, and the queue's metrics count neither an abandon nor a second offer.
            if (IsHeldVisit(session, queueName, entry, reportedJoin))
                return;

            // Every other join opens a new visit, not announced and not left yet, which starts when
            // Asterisk says the caller joined: for a caller a queue snapshot reports waiting, now minus the
            // wait it reports, and otherwise now. The one start stamps both the visit and CallQueuedEvent,
            // so the queue's metrics and the histogram measure the visit from the same moment.
            visitStartedAt = reportedJoin ?? now;
            session.QueueName = queueName;
            session.QueueVisitAnnounced = false;
            session.QueueVisitLeft = false;
            session.QueueVisitStartedAt = visitStartedAt;
            session.TryTransition(CallSessionState.Queued);
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.QueueJoined, entry.Channel, null, queueName));
        }

        _events.OnNext(new CallQueuedEvent(session.SessionId, session.ServerId,
            visitStartedAt, queueName, entry.Position));
        _ = PersistAsync(session);
    }

    /// <summary>
    /// Marks the caller's current queue visit as left when Asterisk reports the caller leaving that visit's
    /// queue. Only <see cref="IsHeldVisit"/> reads the mark. The leave closes nothing in the queue's metrics,
    /// which still close an unanswered visit at the next join or at the hangup; and app_queue sends the leave
    /// before it connects the caller, so the mark never keeps a visit from being announced.
    /// <para>
    /// A reconnect clears Live's queue table without raising <see cref="QueueManager.CallerLeft"/>, so a
    /// caller the reload then finds still waiting keeps its visit open, which is what lets the reload
    /// recognise it. A reconnect that announced those removals as departures would mark every open visit
    /// left, and every caller a reload finds waiting would count as an abandon and a second offer.
    /// </para>
    /// </summary>
    private void OnQueueCallerLeft(string queueName, AsteriskQueueEntry entry)
    {
        var session = FindByChannelName(entry.Channel);
        if (session is null) return;

        lock (session.SyncRoot)
        {
            if (string.Equals(session.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
                session.QueueVisitLeft = true;
        }
    }

    /// <summary>
    /// The session one of whose participants is on the channel named <paramref name="channel"/>, which is how
    /// Live's queue events name the caller; <c>null</c> when this manager holds none.
    /// </summary>
    private CallSession? FindByChannelName(string channel) =>
        _byChannelId.Values.FirstOrDefault(s => s.Participants.Any(p => p.Channel == channel));

    /// <summary>
    /// How far after the start of the visit this manager holds open a queue snapshot may place the caller's
    /// join and still be reporting that same visit: the last condition of <see cref="IsHeldVisit"/>.
    /// <para>
    /// The join a snapshot places is the reload instant minus the <c>Wait</c> Asterisk reports, and Asterisk
    /// counts that wait in whole seconds. For a caller that never left, the join lands within about a second
    /// of the one the manager saw, plus the difference between how long the two reports took to reach it. A
    /// re-join restarts app_queue's wait, so for a caller that left and re-joined the queue while the SDK was
    /// disconnected, the join lands well after.
    /// </para>
    /// <para>
    /// Measured on Asterisk 20.20.1, 22.9.0 and 23.4.1, with a queue that times the caller out after 4 s, plays
    /// a 2 s announcement and puts it back into the same queue, while the SDK's AMI connection was cut and
    /// restored: the join the snapshot placed was between 1.0 s before and 0.1 s after the held start for a
    /// caller that never left (120 reloads), and between 5.5 s and 18.6 s after it for one that had re-joined
    /// (360 reloads). 2 s keeps a margin of about 1.9 s on the first side, and lies 3.5 s below the shortest
    /// re-join measured. A quicker loop is still taken for the same visit: a 1 s queue timeout with no
    /// announcement re-joins about 1.0 s after the first join, on the same three versions. The value is
    /// Asterisk's rounding plus delivery delay, not a deployment choice, which is why it is not an option.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan SameVisitWaitTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a queue entry for <paramref name="queueName"/> reports the visit <paramref name="session"/>
    /// already holds open, rather than a new one. Called under the session's lock. It does only when all of
    /// these hold:
    /// <list type="number">
    /// <item>The entry came from a queue snapshot. A live join is app_queue's report that the caller has just
    /// entered the queue, and app_queue never sends one while the caller's previous visit has not left.</item>
    /// <item>It is for the visit's queue, compared as Live keys queues (ordinal, ignoring case). A report of
    /// another queue is a move this manager did not see.</item>
    /// <item>This manager saw the visit open (<see cref="CallSession.QueueVisitStartedAt"/> is set). A session
    /// restored from elsewhere names its queue, but no visit of it was counted here.</item>
    /// <item>The visit has not been announced (<see cref="CallSession.QueueVisitAnnounced"/>). A visit the
    /// queue connected is over, and a later report of the queue is the caller put back into it.</item>
    /// <item>Asterisk has not reported the caller leaving the queue since the visit opened
    /// (<see cref="CallSession.QueueVisitLeft"/>). app_queue closes every visit with that leave, so a later
    /// report is a re-join.</item>
    /// <item>The join the entry reports, <paramref name="reportedJoin"/>, is no more than
    /// <see cref="SameVisitWaitTolerance"/> after the visit's start. An entry that reports no join (no
    /// <c>Wait</c>, or a negative one) meets it.</item>
    /// </list>
    /// So a caller that left and re-joined the same queue while the SDK was disconnected, whose snapshot
    /// reports no wait or a join within the tolerance, is taken for the visit held: one visit, where Asterisk
    /// counts two and an abandon. Neither its leave nor its re-join reached the SDK.
    /// </summary>
    private static bool IsHeldVisit(CallSession session, string queueName, AsteriskQueueEntry entry,
        DateTimeOffset? reportedJoin) =>
        entry.FromSnapshot
        && string.Equals(session.QueueName, queueName, StringComparison.OrdinalIgnoreCase)
        && session.QueueVisitStartedAt is { } heldStart
        && !session.QueueVisitAnnounced
        && !session.QueueVisitLeft
        && (reportedJoin is not { } join || join - heldStart <= SameVisitWaitTolerance);

    /// <summary>
    /// When the caller joined the queue by Asterisk's own account, for a queue entry the manager handles at
    /// <paramref name="now"/>: <paramref name="now"/> minus the wait a queue snapshot reports for the caller
    /// (<c>QueueEntry</c>'s <c>Wait</c>, which Asterisk counts in whole seconds, so within about a second of
    /// the true join).
    /// <para>
    /// <c>null</c> when the entry reports no such wait: a live join, which Asterisk sends as the caller
    /// joins; a snapshot entry with no <c>Wait</c>; a negative one, which Asterisk does not send; and one
    /// reaching back before the earliest instant a <see cref="DateTimeOffset"/> can hold, which Asterisk
    /// cannot have measured either, and which would otherwise fail the whole reload on this one entry.
    /// </para>
    /// </summary>
    private static DateTimeOffset? ReportedJoin(DateTimeOffset now, AsteriskQueueEntry entry) =>
        entry is { FromSnapshot: true, ReportedWaitSeconds: long waitSeconds and >= 0 }
        && waitSeconds <= now.UtcTicks / TimeSpan.TicksPerSecond
            ? now - TimeSpan.FromSeconds(waitSeconds)
            : null;

    /// <summary>
    /// Publishes <see cref="CallConnectedEvent"/> for <paramref name="session"/> and, for a call that
    /// joined a queue, records the visit's wait in the queue wait-time histogram
    /// (<c>sessions.wait_time</c>). Every publication of the event goes through here. Called under the
    /// session's lock.
    /// <list type="bullet">
    /// <item>A call that joined a queue (<see cref="CallSession.QueueName"/> set) is announced once per
    /// visit: the first connect after a join publishes, and every later one until the next join does
    /// nothing. Its sample is the visit's wait, from the visit's start
    /// (<see cref="CallSession.QueueVisitStartedAt"/>) to this connect, both on the manager's clock; a
    /// session whose join the manager never saw (restored from a snapshot) falls back
    /// to the call's wait since it was created, as the queue's metrics do. A wait of zero is a sample;
    /// only a negative one, from a clock stepping back, is dropped.</item>
    /// <item>A call that never joined a queue is published every time, as it always has been, and records
    /// no sample on any path: it has no queue wait.</item>
    /// </list>
    /// The event's own <see cref="CallConnectedEvent.WaitTime"/> is the call's wait since it was
    /// created, whichever the case.
    /// </summary>
    private void PublishConnected(CallSession session, string? agentId)
    {
        var queued = session.QueueName is not null;
        if (queued)
        {
            if (session.QueueVisitAnnounced)
                return;

            session.QueueVisitAnnounced = true;
        }

        var connectedAt = _timeProvider.GetUtcNow();
        var waitSinceCreated = session.WaitTime ?? TimeSpan.Zero;
        _events.OnNext(new CallConnectedEvent(session.SessionId, session.ServerId,
            connectedAt, agentId, session.QueueName, waitSinceCreated));

        if (!queued)
            return;

        var visitWait = session.QueueVisitStartedAt is { } visitStartedAt
            ? connectedAt - visitStartedAt
            : waitSinceCreated;
        if (visitWait >= TimeSpan.Zero)
            SessionMetrics.WaitTimeMs.Record(visitWait.TotalMilliseconds);
    }

    /// <summary>
    /// Delivers the ending of a call whose participants have all left: its release-queue entry, its
    /// tracing span, its completion measurements and its <see cref="CallEndedEvent"/>. Runs under the
    /// session's lock, from <see cref="OnChannelRemoved"/>.
    /// <para>
    /// Every participant can be found to have left more than once: a leg that joined the call after it
    /// ended — reusing its <c>linkedid</c>, or admitted into it by the reload that ended it — leaves
    /// too. The ending is delivered only the first time, keyed on the session's own delivery marker,
    /// never on its state (<c>ADR-0063</c>, D2). A repeat saves the session, which now records the late
    /// leg — and, like every save, only while this manager still holds that same object
    /// (<see cref="PersistAsync"/>): once it has been released, a save would hand the store a call the
    /// SDK has let go of.
    /// </para>
    /// </summary>
    private void OnSessionCompleted(CallSession session)
    {
        if (!session.TryMarkEndingDelivered())
        {
            _ = PersistAsync(session);
            return;
        }

        QueueForRelease(session);

        // Record tracing span
        using var activity = SessionActivitySource.StartSessionCompleted(
            session.SessionId, session.Direction, session.State, session.Duration);

        // Record metrics
        SessionMetrics.DurationMs.Record(session.Duration.TotalMilliseconds);
        if (session.TalkTime.HasValue)
            SessionMetrics.TalkTimeMs.Record(session.TalkTime.Value.TotalMilliseconds);
        if (session.HoldTime > TimeSpan.Zero)
            SessionMetrics.HoldTimeMs.Record(session.HoldTime.TotalMilliseconds);

        switch (session.State)
        {
            case CallSessionState.Completed:
                SessionMetrics.SessionsCompleted.Add(1);
                break;
            case CallSessionState.Failed:
                SessionMetrics.SessionsFailed.Add(1);
                break;
            case CallSessionState.TimedOut:
                SessionMetrics.SessionsTimedOut.Add(1);
                break;
        }

        _events.OnNext(new CallEndedEvent(session.SessionId, session.ServerId,
            DateTimeOffset.UtcNow, session.HangupCause, session.Duration, session.TalkTime));

        _ = PersistAsync(session);
        EvictStaleCompleted();
    }

    /// <summary>
    /// Whether this manager still holds <paramref name="session"/> itself — not merely a session with
    /// the same id.
    /// </summary>
    private bool IsHeld(CallSession session) =>
        _sessions.TryGetValue(session.SessionId, out var held) && ReferenceEquals(held, session);

    /// <summary>
    /// How many entries the release queue holds for <paramref name="sessionId"/>: the number of times
    /// the session's ending was queued and not yet walked past. Read by tests, which have no other way
    /// to see the queue; nothing in the manager reads it.
    /// </summary>
    internal int ReleaseQueueEntriesFor(string sessionId) =>
        _completedOrder.Count(entry => string.Equals(entry.Session.SessionId, sessionId, StringComparison.Ordinal));

    /// <summary>
    /// Queues <paramref name="session"/>, whose ending has just been delivered, for release once
    /// <see cref="SessionOptions.CompletedRetention"/> has passed since its completion time — the time
    /// it carries <em>now</em>, recorded with the entry.
    /// <para>
    /// Internal rather than private so a test can put at the head an entry no route of the manager
    /// produces, and show that the walk gets past it: the walk's progress must not depend on how an
    /// entry went bad.
    /// </para>
    /// </summary>
    internal void QueueForRelease(CallSession session) =>
        _completedOrder.Enqueue(new ReleaseEntry(session, session.CompletedAt));

    /// <summary>
    /// Releases every queued call past <see cref="SessionOptions.CompletedRetention"/>, oldest first.
    /// Runs on every arrival, before the arriving leg is correlated, and on every ending, after its
    /// <see cref="CallEndedEvent"/>; nothing schedules it, and one walk runs at a time.
    /// <para>
    /// An entry leaves the queue first and is judged afterwards. The one thing read before it leaves
    /// is its own record: a completion time that proves the call ended inside retention keeps it, and
    /// the walk stops there. Every other entry is taken off — one naming a session no longer held, one
    /// with no completion time, one past retention — so no entry can hold the head, and with it every
    /// call queued after it, for the life of the process. That was the wedge: the walk used to take an
    /// entry off only once it could evaluate it, and stopped at the first it could not (<c>ADR-0063</c>,
    /// D1).
    /// </para>
    /// </summary>
    internal void EvictStaleCompleted()
    {
        lock (_releaseLock)
        {
            var cutoff = _timeProvider.GetUtcNow() - _options.CompletedRetention;

            // Under the lock only this walk dequeues, so the entry taken off is the head just read.
            while (_completedOrder.TryPeek(out var head) && !head.EndedWithin(cutoff)
                   && _completedOrder.TryDequeue(out var entry))
            {
                // No completion time: the entry says nothing about how old the call is, so it is
                // dropped and the call kept. Holding is the direction this bound fails in; releasing
                // on no evidence is not.
                if (entry.CompletedAt is null)
                    continue;

                Release(entry.Session);
            }
        }
    }

    /// <summary>
    /// Lets go of an ended session past retention. Each index gives up its entry only while that entry
    /// is this very session object, so an entry naming a session no longer held — released by an
    /// earlier entry for the same call — changes nothing, and a newer session reusing the id or the
    /// <c>linkedid</c> is never removed on an older one's account.
    /// <para>
    /// The store is told last, once the session is out of <c>_sessions</c>, so no save that checks
    /// afterwards can find it held. The default in-memory store keeps this same object and lets go of
    /// it; a durable store keeps its own retention and does nothing (<c>ADR-0063</c>, D5).
    /// </para>
    /// </summary>
    private void Release(CallSession session)
    {
        _sessions.TryRemove(new KeyValuePair<string, CallSession>(session.SessionId, session));
        _byLinkedId.TryRemove(new KeyValuePair<string, CallSession>(session.LinkedId, session));
        _store.OnReleasedByManager(session);
    }

    /// <summary>
    /// One delivered ending waiting for release: the session it ended, and the completion time that
    /// session carried when the ending was delivered. The walk judges the entry by that recorded time,
    /// never by reading the session again, so nothing done to a session after its ending — a consumer
    /// clearing or moving its public <see cref="CallSession.CompletedAt"/> — can make an entry hold the
    /// queue.
    /// </summary>
    private readonly record struct ReleaseEntry(CallSession Session, DateTimeOffset? CompletedAt)
    {
        /// <summary>
        /// Whether this entry's own record proves its call ended at or after <paramref name="cutoff"/>,
        /// that is, inside retention. An entry with no completion time proves nothing and answers no.
        /// </summary>
        public bool EndedWithin(DateTimeOffset cutoff) => CompletedAt >= cutoff;
    }

    public bool RegisterReconstructedSession(CallSession session)
    {
        if (!_byLinkedId.TryAdd(session.LinkedId, session))
            return false;

        _sessions.TryAdd(session.SessionId, session);

        foreach (var participant in session.Participants)
            _byChannelId.TryAdd(participant.UniqueId, session);

        if (session.BridgeId is not null)
            _bridgeToSession.TryAdd(session.BridgeId, session.SessionId);

        _ = PersistAsync(session);

        return true;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var serverId in _serverSubs.Keys.ToArray())
            DetachFromServer(serverId);
        _events.OnCompleted();
        _events.Dispose();

        // Withdraws the gauges. Their callbacks hold this manager, and a meter stays published until it
        // is disposed, so an undisposed one would keep every call the manager held reachable.
        _residencyMeter.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Agent event handlers ---

    private void OnAgentConnected(string agentId, string? linkedId, string? interface_)
    {
        if (linkedId is null || !_byLinkedId.TryGetValue(linkedId, out var session))
            return;

        lock (session.SyncRoot)
        {
            session.AgentId = agentId;
            session.AgentInterface = interface_;
            session.AddEvent(new CallSessionEvent(
                DateTimeOffset.UtcNow,
                CallSessionEventType.AgentConnected,
                interface_,
                null,
                $"Agent {agentId}"));

            if (session.TryTransition(CallSessionState.Connected))
            {
                session.ConnectedAt ??= DateTimeOffset.UtcNow;
            }

            // A queued call is announced once per visit, and a known agent's connect arrives before
            // app_queue's own report of it (OnQueueCallerConnected), so this is the announcement that
            // carries the agent. A call the manager never saw join a queue is published as it always was.
            PublishConnected(session, agentId);
        }

        _ = PersistAsync(session);
    }

    /// <summary>
    /// app_queue connected a queue caller to a member (<c>AgentConnect</c>), whether or not the member is
    /// an agent the SDK knows by name: the queue visit is answered. The caller is looked up by its own
    /// channel, <paramref name="callerUniqueId"/>, which a <c>Linkedid</c> rewrite does not move.
    /// <para>
    /// Only a call the manager saw join a queue is acted on. For one it did not see join
    /// (<see cref="CallSession.QueueName"/> unset), the queue's metrics never counted an offer, so
    /// counting an answer would make answered exceed offered; such a call is announced, as it always was,
    /// by a known agent's connect or by a bridge.
    /// </para>
    /// </summary>
    private void OnQueueCallerConnected(string? callerUniqueId, string? memberName, string? memberInterface)
    {
        if (callerUniqueId is null || !_byChannelId.TryGetValue(callerUniqueId, out var session))
            return;

        lock (session.SyncRoot)
        {
            if (session.QueueName is null)
                return;

            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.AgentConnected, memberInterface, null, memberName));

            if (session.TryTransition(CallSessionState.Connected))
                session.ConnectedAt ??= DateTimeOffset.UtcNow;

            PublishConnected(session, session.AgentId);
        }

        _ = PersistAsync(session);
    }

    // --- Dial event handlers ---

    private void OnChannelDialBegin(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Dialing))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Dialing, channel.Name, channel.DialedChannel, null));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnChannelDialEnd(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.Connected, channel.Name, null, $"dial:{channel.DialStatus}"));

            if (channel.DialStatus == "ANSWER")
                session.TryTransition(CallSessionState.Connected);
        }
        _ = PersistAsync(session);
    }

    // --- Bridge destroyed handler ---

    private void OnBridgeDestroyed(AsteriskBridge bridge)
    {
        if (_bridgeToSession.TryRemove(bridge.BridgeUniqueid, out var sessionId) &&
            _sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.SyncRoot)
            {
                if (session.BridgeId == bridge.BridgeUniqueid)
                    session.BridgeId = null;
            }
        }
    }

    // Subscription management: stores typed delegates for proper -= unsubscribe
    private sealed class ServerSubscriptions(
        VerbaraServer server,
        Action<AsteriskChannel> onAdded,
        Action<AsteriskChannel> onRemoved,
        Action<AsteriskChannel> onStateChanged,
        Action<AsteriskChannel> onDialBegin,
        Action<AsteriskChannel> onDialEnd,
        Action<AsteriskChannel> onHeld,
        Action<AsteriskChannel> onUnheld,
        Action<AsteriskBridge, string> onBridgeEntered,
        Action<AsteriskBridge> onBridgeDestroyed,
        Action<BridgeTransferInfo> onTransfer,
        Action<string, AsteriskQueueEntry> onCallerJoined,
        Action<string, AsteriskQueueEntry> onCallerLeft,
        Action<string, string?, string?> onAgentConnected,
        Action<string?, string?, string?> onQueueCallerConnected)
    {
        public void Detach()
        {
            server.Channels.ChannelAdded -= onAdded;
            server.Channels.ChannelRemoved -= onRemoved;
            server.Channels.ChannelStateChanged -= onStateChanged;
            server.Channels.ChannelDialBegin -= onDialBegin;
            server.Channels.ChannelDialEnd -= onDialEnd;
            server.Channels.ChannelHeld -= onHeld;
            server.Channels.ChannelUnheld -= onUnheld;
            server.Bridges.ChannelEntered -= onBridgeEntered;
            server.Bridges.BridgeDestroyed -= onBridgeDestroyed;
            server.Bridges.TransferOccurred -= onTransfer;
            server.Queues.CallerJoined -= onCallerJoined;
            server.Queues.CallerLeft -= onCallerLeft;
            server.Agents.AgentConnected -= onAgentConnected;
            server.Agents.QueueCallerConnected -= onQueueCallerConnected;
        }
    }
}
