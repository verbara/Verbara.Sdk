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

public sealed partial class CallSessionManager : ICallSessionManager, IQueueVisitSource
{
    private readonly ConcurrentDictionary<string, CallSession> _sessions = new();
    private readonly ConcurrentDictionary<string, CallSession> _byLinkedId = new();
    private readonly ConcurrentDictionary<string, CallSession> _byChannelId = new();
    private readonly ConcurrentDictionary<string, string> _bridgeToSession = new();

    /// <summary>
    /// The unordered server pairs already reported at Warning for sharing a channel id or a
    /// <c>linkedid</c>, each pair ordered ordinally so <c>{a, b}</c> and <c>{b, a}</c> are one key. Its keys
    /// are built only from server ids the host passed to <see cref="AttachToServer"/>, never from ids read
    /// off the wire, so call traffic cannot grow it. It is never cleared: the Warning is once per pair for
    /// the manager's life, and a server that comes back under the same id has the same cause.
    /// </summary>
    private readonly ConcurrentDictionary<(string Low, string High), byte> _warnedServerPairs = new();
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
    /// the ended calls whose release is pending, and some ended calls are held with no entry: one
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
            count += HasEnded(held.Value) == ended ? 1 : 0;

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
        Action<string, AsteriskQueueEntry> onCallerJoined = (queueName, entry) => OnQueueCallerJoined(queueName, entry, server);
        Action<QueueCallerLeaveReport> onCallerLeft = OnQueueCallerLeft;
        Action<string, string> onAbandonReported = OnQueueCallerAbandonReported;
        Action<string, string, string> onQueueStatus = OnCallerQueueStatus;
        Action<QueueSnapshotCompletion> onSnapshotCompleted = completion => OnQueueSnapshotCompleted(completion, serverId);
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
        server.Queues.CallerLeaveReported += onCallerLeft;
        server.Queues.CallerAbandonReported += onAbandonReported;
        server.Queues.CallerQueueStatus += onQueueStatus;
        server.Queues.QueueSnapshotCompleted += onSnapshotCompleted;
        server.Agents.AgentConnected += onAgentConnected;
        server.Agents.QueueCallerConnected += onQueueCallerConnected;

        _serverSubs[serverId] = new ServerSubscriptions(server,
            onAdded, onRemoved, onStateChanged, onDialBegin, onDialEnd,
            onHeld, onUnheld, onBridgeEntered, onBridgeDestroyed, onTransfer, onCallerJoined,
            onCallerLeft, onAbandonReported, onQueueStatus, onSnapshotCompleted, onAgentConnected, onQueueCallerConnected);
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

    /// <summary>
    /// Puts an arriving leg into its call: the call its <c>linkedid</c> names, or a call of its own.
    /// <para>
    /// One call per <c>linkedid</c>, whichever thread reports each leg: the snapshot route and the live
    /// route can deliver two legs of one call at the same moment, so finding the call and opening it
    /// are one atomic step. The leg's would-be session is built first and then published with
    /// <c>TryAdd</c> on the <c>linkedid</c> index; exactly one arrival wins, and the loser's session is
    /// withdrawn unannounced and the leg looks again, this time finding the winner and joining it. The
    /// look is repeated rather than assumed, because the release walk can let go of the call it found
    /// in between. A session is written to the id index before it is published by <c>linkedid</c>, so a
    /// leg that joins it never indexes or saves a session the manager does not hold.
    /// </para>
    /// <para>
    /// A leg that arrives after its call has ended opens a new call: an ended call's state is terminal,
    /// so it could never report that leg's ending. The ended call keeps its participants and its one
    /// ending, and the <c>linkedid</c> resolves to the new call from then on. Whether the call has ended
    /// is read under its lock, the lock its ending is taken under, so a leg never joins a call that ends
    /// before it is added.
    /// </para>
    /// <para>
    /// A leg that is already a present participant of the call — reported again, by a second server
    /// watching the same channel — adds nothing: no participant, no audit entry, no save.
    /// </para>
    /// </summary>
    private void OnChannelAdded(AsteriskChannel channel, string serverId)
    {
        // Release rides arrivals as well as endings, so a process that keeps accepting calls but has
        // stopped completing them still lets go of what it holds past retention. It runs before the
        // leg is correlated, so the leg is not put into a call this same evaluation lets go of. No
        // timer is involved, so an idle process releases nothing.
        EvictStaleCompleted();

        var linkedId = channel.LinkedId;
        if (string.IsNullOrEmpty(linkedId)) linkedId = channel.UniqueId;

        // The indexes are shared by every attached server, so an id another server's call holds is a
        // collision of the indexes. It is judged here, before this arrival writes the channel-id index,
        // and again on the linkedid entry the loop below is about to join or replace; at most one entry
        // per arrival. Only the held session's server decides it, never the spelling of the id, and
        // nothing about where the leg goes changes.
        var collisionReported = false;
        if (_byChannelId.TryGetValue(channel.UniqueId, out var holder)
            && !string.Equals(holder.ServerId, serverId, StringComparison.Ordinal))
        {
            ReportCrossServerCollision(serverId, holder.ServerId, ChannelIdKind, channel.UniqueId);
            collisionReported = true;
        }

        // The session this leg opens if it finds no live call to join. Built once, fully, before it can
        // be published, so whoever finds it through the linkedid index sees a complete session.
        var direction = _correlator.InferDirection(channel.Context, channel.Extension);
        var session = new CallSession(Guid.NewGuid().ToString("N"), linkedId, serverId, direction);

        session.Context = channel.Context;
        session.Extension = channel.Extension;

        // A channel that came out of a Status snapshot — a first load or a post-reconnect reload —
        // is a call the SDK never saw start. The state on it is Asterisk's own account of the call
        // and the only one that will ever arrive: no NewState announcing it is coming, because it
        // already happened. Opening such a call in Created would report a live conversation as one
        // that has not started.
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

        while (true)
        {
            if (_byLinkedId.TryGetValue(linkedId, out var existing))
            {
                if (!collisionReported && !string.Equals(existing.ServerId, serverId, StringComparison.Ordinal))
                {
                    ReportCrossServerCollision(serverId, existing.ServerId, LinkedIdKind, linkedId);
                    collisionReported = true;
                }

                if (TryJoin(existing, channel))
                    return;

                // The call this leg's linkedid names has ended: the leg opens its own call in its place,
                // provided the index still names that ended call.
                _sessions[session.SessionId] = session;
                if (_byLinkedId.TryUpdate(linkedId, session, existing))
                    break;
            }
            else
            {
                _sessions[session.SessionId] = session;
                if (_byLinkedId.TryAdd(linkedId, session))
                    break;
            }

            // Another arrival published a call for this linkedid first, or the release walk let go of
            // the ended one. This session was never announced, counted or saved; withdraw it and look again.
            _sessions.TryRemove(new KeyValuePair<string, CallSession>(session.SessionId, session));
        }

        _byChannelId[channel.UniqueId] = session;
        SessionMetrics.SessionsCreated.Add(1);

        _events.OnNext(new CallStartedEvent(session.SessionId, serverId,
            DateTimeOffset.UtcNow, direction, channel.CallerIdNum));

        _ = PersistAsync(session);
    }

    private const string ChannelIdKind = "channel id";
    private const string LinkedIdKind = "linkedid";

    /// <summary>
    /// Logs that <paramref name="serverId"/> reported <paramref name="id"/> while the session indexes hold
    /// it for a call of <paramref name="otherServerId"/>: at Warning the first time this unordered pair of
    /// servers collides, at Debug every time after. The lock-free look comes first because in a pool
    /// whose Asterisk servers share an id space nearly every arrival collides, and an insert takes the
    /// key's lock even when the key is already there; the insert alone decides the Warning, so two
    /// servers colliding on two threads at once still log it once.
    /// </summary>
    private void ReportCrossServerCollision(string serverId, string otherServerId, string idKind, string id)
    {
        var pair = string.CompareOrdinal(serverId, otherServerId) < 0
            ? (serverId, otherServerId)
            : (otherServerId, serverId);

        if (!_warnedServerPairs.ContainsKey(pair) && _warnedServerPairs.TryAdd(pair, 0))
            LogCrossServerCollision(serverId, idKind, id, otherServerId);
        else
            LogCrossServerCollisionAgain(serverId, idKind, id, otherServerId);
    }

    [LoggerMessage(EventId = 1, EventName = nameof(LogCrossServerCollision), Level = LogLevel.Warning,
        Message = "Server {ServerId} reported {IdKind} {Id}, which the session indexes already hold for a call of "
            + "server {OtherServerId}. The session manager shares one index across servers, so calls of these two "
            + "servers can be correlated together. Asterisk channel ids are unique across servers only when each "
            + "Asterisk has its own 'systemname' in asterisk.conf; if both server ids watch the same Asterisk, "
            + "attach it once. Later collisions between these two servers are logged at Debug")]
    private partial void LogCrossServerCollision(string serverId, string idKind, string id, string otherServerId);

    [LoggerMessage(EventId = 2, EventName = nameof(LogCrossServerCollisionAgain), Level = LogLevel.Debug,
        Message = "Server {ServerId} reported {IdKind} {Id}, already held for a call of server {OtherServerId}; "
            + "collision between these servers already reported at Warning")]
    private partial void LogCrossServerCollisionAgain(string serverId, string idKind, string id, string otherServerId);

    /// <summary>
    /// Adds <paramref name="channel"/> to <paramref name="call"/> unless the call has ended, and answers
    /// whether the leg now belongs to it. A leg already present in the call is not added again.
    /// </summary>
    private bool TryJoin(CallSession call, AsteriskChannel channel)
    {
        lock (call.SyncRoot)
        {
            if (HasEnded(call))
                return false;

            if (call.Participants.Any(p => p.UniqueId == channel.UniqueId && !p.LeftAt.HasValue))
                return true;

            var role = SessionCorrelator.InferRole(channel.Name, call.Participants.Count);
            call.AddParticipant(new SessionParticipant
            {
                UniqueId = channel.UniqueId,
                Channel = channel.Name,
                Technology = SessionCorrelator.ExtractTechnology(channel.Name),
                Role = role,
                CallerIdNum = channel.CallerIdNum,
                CallerIdName = channel.CallerIdName,
                JoinedAt = DateTimeOffset.UtcNow
            });
            call.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.ParticipantJoined, channel.Name, null, role.ToString()));
        }

        _byChannelId[channel.UniqueId] = call;
        _ = PersistAsync(call);
        return true;
    }

    /// <summary>
    /// The metadata key under which the SDK records how a session ended when it did <em>not</em>
    /// observe the ending. The SDK writes one value under it, <see cref="EndingProvenanceReload"/>. A
    /// session that a version before 2.7.0 ended from its reconciliation sweep, read back from a store,
    /// may carry <c>"orphaned"</c> under the same key; the SDK no longer writes that value.
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
            // The leg still present: a leg with this id that already left keeps its own departure.
            var participant = session.Participants.FirstOrDefault(
                p => p.UniqueId == channel.UniqueId && !p.LeftAt.HasValue);
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
                    // never became a call. A call is up when it connected, and also when the SDK
                    // observed its answer while it was still in its initial state, with no dial or
                    // queue reaching it (an IVR, an originate answered with no dial onward): that one
                    // ends completed from its observed answer. Transferring is deliberately absent —
                    // it has no valid transition to Completed — and falls to the Failed arm below.
                    if (!session.TryCompleteAnsweredInInitialState())
                    {
                        var reloadTarget = session.State is CallSessionState.Connected
                            or CallSessionState.OnHold or CallSessionState.Conference
                            ? CallSessionState.Completed
                            : CallSessionState.Failed;

                        if (!session.TryTransition(reloadTarget))
                            session.TryTransition(CallSessionState.Failed);
                    }

                    LogEndedByReload(session.SessionId, session.LinkedId, session.State);
                }
                else
                {
                    var targetState = channel.HangupCause == HangupCause.NormalClearing
                        ? CallSessionState.Completed
                        : CallSessionState.Failed;

                    // Try the natural progression if needed. A call still in its initial state ends
                    // failed, unless the SDK observed its answer and it hung up normally: then the
                    // dialplan answered it, no dial or queue reached it, and it is a completed call.
                    if (session.State == CallSessionState.Created)
                    {
                        if (targetState != CallSessionState.Completed
                            || !session.TryCompleteAnsweredInInitialState())
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
                    // A queued call waits on app_queue: a member's leg that answers, carrying the
                    // caller's linked id, is not the queue connecting it. Only app_queue's own report
                    // does that (OnQueueCallerConnected, OnAgentConnected).
                    if (session.State == CallSessionState.Queued)
                        break;

                    if (session.TryTransition(CallSessionState.Connected))
                    {
                        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                            CallSessionEventType.Connected, channel.Name, null, null));
                        changed = true;
                    }
                    else
                    {
                        // The answer of a call still in its initial state (an IVR, an originate
                        // answered with no dial onward) moves nothing: it is recorded, and read only
                        // when the call ends. Nothing is published, counted or saved here.
                        session.RecordAnswerInInitialState(DateTimeOffset.UtcNow);
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

            // A queued call is connected only by app_queue's report of it (OnQueueCallerConnected,
            // OnAgentConnected): a member leg can enter a bridge that app_queue never connects, as a
            // pooled agent that never acknowledges does. The bridge is still recorded above.
            if (session.State == CallSessionState.Queued)
                return;

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

    /// <summary>
    /// A caller joined <paramref name="queueName"/>, live or as a queue snapshot reports it. The call is found by the
    /// caller channel's Uniqueid, which the join and the snapshot entry carry and a channel rename does not change. An
    /// entry added through Live's public join, which carries none, is resolved through Live's channel table by the
    /// channel's current name.
    /// </summary>
    private void OnQueueCallerJoined(string queueName, AsteriskQueueEntry entry, VerbaraServer server)
    {
        var uniqueId = entry.UniqueId ?? server.Channels.GetByName(entry.Channel)?.UniqueId;
        if (uniqueId is null || !_byChannelId.TryGetValue(uniqueId, out var session)) return;

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
            session.QueueVisitAbandonReported = false;
            session.QueueVisitStartedAt = visitStartedAt;
            session.QueueVisitLossEpoch = entry.LossEpoch ?? server.ReadEventLossEpoch();
            session.QueueVisitJoinOrdinal = entry.JoinOrdinal;
            session.QueueVisitCallerChannel = entry.Channel;
            session.QueueVisitCallerUniqueId = uniqueId;
            session.TryTransition(CallSessionState.Queued);
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.QueueJoined, entry.Channel, null, queueName));
        }

        _events.OnNext(new CallQueuedEvent(session.SessionId, session.ServerId,
            visitStartedAt, queueName, entry.Position));
        _ = PersistAsync(session);
    }

    /// <inheritdoc/>
    internal event Action<QueueVisitSignal>? VisitLeft;

    /// <inheritdoc/>
    internal event Action<QueueVisitSignal>? VisitAbandonReported;

    /// <inheritdoc/>
    internal event Action<QueueVisitSignal>? VisitTimedOut;

    event Action<QueueVisitSignal>? IQueueVisitSource.VisitLeft
    {
        add => VisitLeft += value;
        remove => VisitLeft -= value;
    }

    event Action<QueueVisitSignal>? IQueueVisitSource.VisitAbandonReported
    {
        add => VisitAbandonReported += value;
        remove => VisitAbandonReported -= value;
    }

    event Action<QueueVisitSignal>? IQueueVisitSource.VisitTimedOut
    {
        add => VisitTimedOut += value;
        remove => VisitTimedOut -= value;
    }

    /// <summary>
    /// The caller's current queue visit, under the session's lock, when <paramref name="queueName"/> (if given) is its
    /// queue and this manager opened it: the visit's start, which names it in every signal. A session registered
    /// already in a queue carries the queue's name but no visit this manager opened, and gets <see langword="null"/>.
    /// </summary>
    private static DateTimeOffset? CurrentVisit(CallSession session, string? queueName) =>
        session.QueueName is not null
        && (queueName is null || string.Equals(session.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
            ? session.QueueVisitStartedAt
            : null;

    /// <summary>
    /// Asterisk's report that the caller left a queue (<c>QueueCallerLeave</c>), read from the report itself whether or
    /// not Live's queue table still held the caller, and found by the caller's Uniqueid. When it is the queue of the
    /// call's current visit, the visit is marked left (<see cref="CallSession.QueueVisitLeft"/>, which
    /// <see cref="IsHeldVisit"/> reads) and <see cref="VisitLeft"/> is raised, once per visit: the leave ends the visit's
    /// wait in the queue's metrics. The signal carries whether app_queue reported the visit abandoned before it, and
    /// whether the SDK may have lost events since the visit opened: the event-loss epoch of the leave differs from the
    /// one recorded when the visit opened (a reconnect, or an event the AMI connection's full buffer dropped).
    /// <para>
    /// A reconnect clears Live's queue table without any leave, raw or public, so a caller the reload then finds still
    /// waiting keeps its visit open, which is what lets the reload recognise it. app_queue sends the leave before it
    /// connects the caller, so the mark never keeps a visit from being announced.
    /// </para>
    /// </summary>
    private void OnQueueCallerLeft(QueueCallerLeaveReport report)
    {
        if (!_byChannelId.TryGetValue(report.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (!string.Equals(session.QueueName, report.Queue, StringComparison.OrdinalIgnoreCase)
                || session.QueueVisitLeft)
            {
                return;
            }

            session.QueueVisitLeft = true;
            if (CurrentVisit(session, report.Queue) is not { } visit)
                return;

            var mayHaveLostEvents = session.QueueVisitLossEpoch is not { } opened || opened != report.LossEpoch;
            VisitLeft?.Invoke(new QueueVisitSignal(session.SessionId, session.QueueName!, visit,
                session.QueueVisitAbandonReported, mayHaveLostEvents));
        }
    }

    /// <summary>
    /// app_queue's report that it counted the caller's visit abandoned (<c>QueueCallerAbandon</c>), found by the
    /// caller's Uniqueid: the current visit, when it is in that queue and not connected, is marked abandon-reported and
    /// <see cref="VisitAbandonReported"/> is raised, once per visit.
    /// </summary>
    private void OnQueueCallerAbandonReported(string uniqueId, string queueName)
    {
        if (!_byChannelId.TryGetValue(uniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (CurrentVisit(session, queueName) is not { } visit
                || session.QueueVisitAnnounced
                || session.QueueVisitAbandonReported)
            {
                return;
            }

            session.QueueVisitAbandonReported = true;
            VisitAbandonReported?.Invoke(new QueueVisitSignal(session.SessionId, session.QueueName!, visit));
        }
    }

    /// <summary>
    /// app_queue's <c>QUEUESTATUS</c> on a channel, found by its Uniqueid: <c>TIMEOUT</c> on the caller of a visit the
    /// queue never connected raises <see cref="VisitTimedOut"/> for that visit. Every other value raises nothing.
    /// </summary>
    private void OnCallerQueueStatus(string uniqueId, string channel, string status)
    {
        if (!string.Equals(status, "TIMEOUT", StringComparison.Ordinal)
            || !_byChannelId.TryGetValue(uniqueId, out var session))
        {
            return;
        }

        lock (session.SyncRoot)
        {
            if (CurrentVisit(session, queueName: null) is not { } visit || session.QueueVisitAnnounced)
                return;

            VisitTimedOut?.Invoke(new QueueVisitSignal(session.SessionId, session.QueueName!, visit));
        }
    }

    /// <summary>
    /// A load's <c>QueueStatus</c> snapshot of <paramref name="serverId"/> completed: every call of that server whose
    /// current visit is still open (not left, not connected, not ended), which joined before the snapshot was asked for
    /// and which the snapshot does not list, was no longer waiting when Asterisk answered. Its leave was never received
    /// (it fell in an outage), so the visit is marked left and <see cref="VisitLeft"/> is raised for it, as a leave
    /// after which events may have been lost. A snapshot that did not complete raises no completion, and a visit that
    /// joined after the request is not closed by it: the snapshot could not list it.
    /// </summary>
    private void OnQueueSnapshotCompleted(QueueSnapshotCompletion completion, string serverId)
    {
        foreach (var session in _sessions.Values)
        {
            lock (session.SyncRoot)
            {
                if (session.ServerId != serverId
                    || HasEnded(session)
                    || CurrentVisit(session, queueName: null) is not { } visit
                    || session.QueueVisitLeft
                    || session.QueueVisitAnnounced
                    || completion.JoinedAfterRequest(session.QueueVisitJoinOrdinal)
                    || completion.Lists(session.QueueVisitCallerUniqueId, session.QueueVisitCallerChannel))
                {
                    continue;
                }

                session.QueueVisitLeft = true;
                VisitLeft?.Invoke(new QueueVisitSignal(session.SessionId, session.QueueName!, visit,
                    session.QueueVisitAbandonReported, EventsMayHaveBeenLost: true, LeaveMissed: true));
            }
        }
    }

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
    /// Every participant can be found to have left more than once: a departure can still be reported
    /// for a call that has already ended, through a channel id that still names it. A leg that arrives
    /// after the call ended does not join it — it opens a call of its own (<see cref="OnChannelAdded"/>).
    /// The ending is delivered only the first time, keyed on the session's own delivery marker, never on
    /// its state. A repeat saves the session — and, like every save, only while this manager still holds
    /// that same object (<see cref="PersistAsync"/>): once it has been released, a save would hand the
    /// store a call the SDK has let go of.
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
        // Held by id before it is published by linkedid, as a session opened by an arriving leg is, so
        // a leg that finds it by linkedid never indexes or saves a session the manager does not hold.
        var added = _sessions.TryAdd(session.SessionId, session);
        if (!_byLinkedId.TryAdd(session.LinkedId, session))
        {
            if (added)
                _sessions.TryRemove(new KeyValuePair<string, CallSession>(session.SessionId, session));
            return false;
        }

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
    /// an agent the SDK knows by name: the queue visit is answered, and the call records the member's
    /// interface as its <see cref="CallSession.AgentInterface"/> unless it already records one. The caller
    /// is looked up by its own channel, <paramref name="callerUniqueId"/>, which a <c>Linkedid</c> rewrite
    /// does not move.
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

            // app_queue's connect report names the member by its interface and, as Asterisk sends it, no
            // agent, so the connect of an agent known by name (which runs first and records its own
            // interface) does not run for it. The member is recorded here, before the call is announced, so
            // a consumer of the announcement reads it. One already recorded is kept: a later queue visit of
            // the same call, taken by another member, does not replace it.
            session.AgentInterface ??= memberInterface;

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

            // app_queue's own Dial() reports ANSWER on the caller before it connects the member, and
            // it can still give up between the two. A queued call is connected only by app_queue's
            // report of the connection (OnQueueCallerConnected, OnAgentConnected); the dial outcome
            // stays in the trail above.
            if (channel.DialStatus == "ANSWER" && session.State != CallSessionState.Queued)
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
        Action<QueueCallerLeaveReport> onCallerLeft,
        Action<string, string> onAbandonReported,
        Action<string, string, string> onQueueStatus,
        Action<QueueSnapshotCompletion> onSnapshotCompleted,
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
            server.Queues.CallerLeaveReported -= onCallerLeft;
            server.Queues.CallerAbandonReported -= onAbandonReported;
            server.Queues.CallerQueueStatus -= onQueueStatus;
            server.Queues.QueueSnapshotCompleted -= onSnapshotCompleted;
            server.Agents.AgentConnected -= onAgentConnected;
            server.Agents.QueueCallerConnected -= onQueueCallerConnected;
        }
    }
}
