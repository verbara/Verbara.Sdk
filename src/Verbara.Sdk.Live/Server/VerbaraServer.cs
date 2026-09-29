using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Verbara.Sdk;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Events.Base;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Bridges;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.MeetMe;
using Verbara.Sdk.Live.Queues;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Server;

internal static partial class VerbaraServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[LIVE] State loaded: channels={Channels} queues={Queues} agents={Agents}")]
    public static partial void InitialStateLoaded(ILogger logger, int channels, int queues, int agents);

    [LoggerMessage(Level = LogLevel.Error, Message = "[LIVE] Connection error")]
    public static partial void ConnectionError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "[LIVE] Connection closed")]
    public static partial void ConnectionClosed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "[LIVE] Reconnected: reloading state")]
    public static partial void Reconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "[LIVE] Reconnect reload failed")]
    public static partial void ReconnectReloadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[LIVE] {Action} not registered yet: Asterisk is still loading its modules; asking again when it reports FullyBooted, or every {IntervalMs} ms")]
    public static partial void ActionNotRegisteredYet(ILogger logger, string action, int intervalMs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[LIVE] {Action} never registered: still refused {BudgetSeconds} s after the load's first refusal, so the load completes without it (Asterisk: {Message}). An AMI user with 'system' in read receives FullyBooted and is not kept waiting")]
    public static partial void ActionNeverRegistered(ILogger logger, string action, int budgetSeconds, string message);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[LIVE] Initial state load interrupted: its AMI session ended and the connection is {State}, so the start returns and the reload that follows the reconnect loads the state ({Reason})")]
    public static partial void InitialLoadInterrupted(ILogger logger, AmiConnectionState state, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[LIVE] Reconnect reload interrupted: its AMI session ended and the connection is {State} ({Reason})")]
    public static partial void ReloadInterrupted(ILogger logger, AmiConnectionState state, string reason);

    /// <summary>The Warning a server logs when its AMI connection announces a loss nobody asked for.</summary>
    internal const string AmiConnectionLostLine = "[LIVE] AMI connection lost: live state is stale until it reconnects and reloads";

    [LoggerMessage(Level = LogLevel.Warning, Message = AmiConnectionLostLine)]
    public static partial void AmiConnectionLost(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "[LIVE] ConnectionLost handler error")]
    public static partial void ConnectionLostHandlerError(ILogger logger, Exception exception);
}

/// <summary>
/// Aggregate root for real-time Asterisk state tracking.
/// Listens to AMI events and maintains live domain objects:
/// channels, queues, agents, and conference rooms.
/// </summary>
public sealed class VerbaraServer : IVerbaraServer
{
    /// <summary>
    /// How long a load waits before it asks again for a request that Asterisk refused because the module answering it
    /// has not registered it yet, when Asterisk has not reported <c>FullyBooted</c> on the session. It is the whole
    /// wait only for an AMI user without <c>system</c> in its read permissions, which never receives the report.
    /// </summary>
    /// <remarks>
    /// Measured on Asterisk 20.20.1, 22.9.0 and 23.4.1, over raw AMI sessions logged in right after a container
    /// restart: <c>QueueStatus</c> starts working 80–131 ms after the login and <c>Agents</c> 50–111 ms after it, and
    /// <c>FullyBooted</c> arrives 17–28 ms after <c>QueueStatus</c> works. At this interval a load that logged in at
    /// the start of that window is answered within one or two more asks.
    /// </remarks>
    internal static readonly TimeSpan NotRegisteredRetryInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long after its first refusal of that kind a load stops asking again, and completes without the refused
    /// state. One budget per load: a load whose two refused modules are both absent waits it once, not once per request.
    /// </summary>
    /// <remarks>
    /// Measured on Asterisk 20.20.1, 22.9.0 and 23.4.1: <c>FullyBooted</c> arrives 101–154 ms after the login, so an
    /// Asterisk still refusing a request 10 s after the first refusal has finished starting, and does not have the
    /// module loaded. Only a user who never receives <c>FullyBooted</c> reaches the budget: with the report, a refusal
    /// after it is taken as final at once, and one before it is asked again when it arrives.
    /// </remarks>
    internal static readonly TimeSpan NotRegisteredRetryBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The start of the <c>Message</c> with which Asterisk refuses an action no module has registered, matched ordinally
    /// and ignoring case.
    /// </summary>
    /// <remarks>
    /// Measured as <c>Response: Error</c> with <c>Message: Invalid/unknown command: QueueStatus. Use Action:
    /// ListCommands to show available commands.</c>, and the same for <c>Agents</c>, on Asterisk 18.26.4, 20.20.1,
    /// 22.9.0 and 23.4.1. Asterisk answers it both while app_queue or app_agent_pool is still loading, and for good when
    /// the module is not loaded at all; only <c>FullyBooted</c> tells the two apart.
    /// </remarks>
    internal const string UnknownCommandPrefix = "Invalid/unknown command";

    private readonly IAmiConnection _connection;
    private readonly ILogger<VerbaraServer> _logger;
    private IDisposable? _subscription;
    private IAriClient? _ariClient;
    private readonly Meter _instanceMeter;

    public ChannelManager Channels { get; }
    public QueueManager Queues { get; }
    public AgentManager Agents { get; }
    public MeetMeManager MeetMe { get; }
    public BridgeManager Bridges { get; }

    /// <summary>Fired when the AMI connection is lost or completed.</summary>
    /// <remarks>
    /// <para>
    /// Raised once for each loss of the established AMI connection that the caller did not ask for, with what
    /// ended it: <see langword="null"/> when the connection's stream ended (Asterisk closed it, or it was reset: the
    /// socket transport reports both as an end of stream), a <see cref="TimeoutException"/> when the heartbeat's Ping
    /// went unanswered, or the reader's exception when the AMI stream could not be read. It is raised whether automatic
    /// reconnection is on or off. From that moment until the connection's <see cref="IAmiConnection.Reconnected"/> and
    /// the reload that follows it, the channels, queues and agents this server holds are not being updated.
    /// </para>
    /// <para>
    /// It is raised after <see cref="IAmiConnection.State"/> has left <see cref="AmiConnectionState.Connected"/>,
    /// and before the <see cref="IAmiConnection.Reconnected"/> of the same outage, which is delivered behind it on the
    /// same ordered queue. It is not raised for the caller's own <c>DisconnectAsync</c> or <c>DisposeAsync</c>, nor a
    /// second time when the reconnect loop gives up at <c>MaxReconnectAttempts</c>: a connection that gave up reads
    /// <see cref="AmiConnectionState.Disconnected"/>. Handlers run on a thread-pool thread, one after the other; they
    /// never hold the connection's reader, heartbeat or reconnect, but this server's reload after the reconnect waits
    /// for them, so a slow one delays it: keep them short. A handler that throws is logged and the others still run.
    /// A handler may end the connection, or remove this server from its pool, and wait for that.
    /// </para>
    /// <para>
    /// That is over the SDK's own <see cref="AmiConnection"/>. Over another <see cref="IAmiConnection"/>, it is raised
    /// when that connection calls <see cref="IObserver{T}.OnError"/> (with the error) or
    /// <see cref="IObserver{T}.OnCompleted"/> (with <see langword="null"/>) on its observers, on the thread that calls
    /// it; the SDK's own connection never calls either, so nothing is raised twice.
    /// </para>
    /// </remarks>
    public event Action<Exception?>? ConnectionLost;

    /// <summary>The underlying AMI connection for this server.</summary>
    public IAmiConnection Connection => _connection;

    /// <summary>The optional ARI client for this server.</summary>
    public IAriClient? AriClient => _ariClient;

    /// <summary>The Asterisk version string.</summary>
    public string? AsteriskVersion => _connection.AsteriskVersion;

    // The clock a load's waits for a starting Asterisk run on: the interval between two asks, and the budget, which
    // is measured with GetTimestamp so that a step of the wall clock cannot stretch or cut it. Settable by tests (via
    // InternalsVisibleTo) to drive both on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public VerbaraServer(IAmiConnection connection, ILogger<VerbaraServer> logger)
    {
        _connection = connection;
        _logger = logger;
        Channels = new ChannelManager(logger);
        Queues = new QueueManager(logger);
        Agents = new AgentManager(logger);
        MeetMe = new MeetMeManager(logger);
        Bridges = new BridgeManager(logger);
        _instanceMeter = new Meter("Verbara.Sdk.Live", "1.0.0");
    }

    /// <summary>
    /// Assign an ARI client to this server post-construction.
    /// Used by cluster orchestrators that create the ARI connection after AMI is established.
    /// </summary>
    public void SetAriClient(IAriClient ariClient) => _ariClient = ariClient;

    /// <summary>
    /// Initialize state by subscribing to AMI events and loading current state.
    /// Call this after the AMI connection is established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asterisk accepts an AMI login before its modules have loaded. Until app_queue and app_agent_pool have registered
    /// their actions it refuses the queues' and the agents' requests as an unknown command, which is also its answer
    /// for good when the module is not loaded. The load does not take such a refusal as an empty table: it asks again
    /// once Asterisk reports <c>FullyBooted</c>, which Asterisk sends only to an AMI user with <c>system</c> in its read
    /// permissions, or every 200 ms for a user without it. A load that logs in while Asterisk starts therefore takes
    /// that much longer. A user without <c>system</c>, on a PBX where app_queue or app_agent_pool is not loaded, waits
    /// 10 s once per load, after which the load completes without what was refused and logs a warning.
    /// </para>
    /// <para>
    /// When the AMI session ends before the load completes and the connection is reconnecting, the start returns
    /// without an exception and logs a warning: the reload that follows the reconnect loads the state, and until then
    /// the managers hold what the load had read. Whether the connection is reconnecting is read from its state when
    /// the load stops, so a reconnect that gives up after the start has returned is not reported by the start.
    /// </para>
    /// </remarks>
    /// <exception cref="AmiNotConnectedException">
    /// The connection was not established when the start began; or its AMI session ended before the load completed
    /// and the connection will not come back, because automatic reconnection is off, the reconnect gave up, or the
    /// caller ended the connection. The state is not reported as loaded.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription = _connection.Subscribe(new EventObserver(this));
        _connection.Reconnected += OnReconnected;
        // Subscribed before the load, so that a loss which cuts the load short is announced too.
        if (_connection is AmiConnection amiConnection)
            amiConnection.Lost += OnConnectionLost;

        // Register observable gauges for live state (all typed as long for consistent metric reporting)
        _instanceMeter.CreateObservableGauge<long>("live.channels.active",
            () => Channels.ChannelCount, description: "Active channels");
        _instanceMeter.CreateObservableGauge<long>("live.queues.count",
            () => Queues.QueueCount, description: "Configured queues");
        _instanceMeter.CreateObservableGauge<long>("live.agents.total",
            () => Agents.AgentCount, description: "Total tracked agents");
        _instanceMeter.CreateObservableGauge<long>("live.agents.available",
            () => Agents.Agents.Count(a => a.State == AgentState.Available),
            description: "Agents in Available state");
        _instanceMeter.CreateObservableGauge<long>("live.agents.on_call",
            () => Agents.Agents.Count(a => a.State == AgentState.OnCall),
            description: "Agents currently on a call");
        _instanceMeter.CreateObservableGauge<long>("live.agents.paused",
            () => Agents.Agents.Count(a => a.State == AgentState.Paused),
            description: "Agents in Paused state");
        _instanceMeter.CreateObservableGauge<long>("live.agents.total_hold_secs",
            () => Agents.Agents.Sum(a => a.TotalHoldTimeSecs),
            unit: "s", description: "Aggregate hold time across all agents since login");
        _instanceMeter.CreateObservableGauge<long>("live.agents.total_talk_secs",
            () => Agents.Agents.Sum(a => a.TotalTalkTimeSecs),
            unit: "s", description: "Aggregate talk time across all agents since login");

        var states = new LoadConnectionStates(_connection);
        try
        {
            await LoadAsync(states, cancellationToken);
        }
        catch (AmiNotConnectedException ex) when (states.ReconnectWillReload)
        {
            // The session ended under the load and the connection is reconnecting: OnReconnected, subscribed above,
            // reloads the state once it has. Failing the start would take down a host that the reconnect repairs.
            VerbaraServerLog.InitialLoadInterrupted(_logger, states.AtStop ?? _connection.State, ex.Message);
        }
    }

    // Justification: event handler for Action delegate requires async void.
    // All exceptions are caught in the try/catch below — no unobserved exceptions.
#pragma warning disable VSTHRD100 // Avoid async void — required by Action event delegate
    private async void OnReconnected()
#pragma warning restore VSTHRD100
    {
        LoadConnectionStates? states = null;
        try
        {
            VerbaraServerLog.Reconnected(_logger);

            // The channel table is deliberately NOT cleared here. Clearing it raises no
            // ChannelRemoved, so every consumer holding call state — CallSessionManager first —
            // was never told the channels went away, and a call that ended during the outage
            // stayed "in progress" for the life of the process. RequestInitialStateAsync now
            // buffers Asterisk's snapshot to completion and reconciles the table against it
            // instead (ADR-0062, design D1/D2), which announces the difference on the event
            // consumers already subscribe to. The four managers below hold no session identity,
            // so they keep their clear-and-reload — an explicit non-goal, not an oversight.
            Queues.Clear();
            Agents.Clear();
            MeetMe.Clear();
            Bridges.Clear();

            // Re-subscribe observer (the connection is new after reconnect)
            _subscription?.Dispose();
            _subscription = _connection.Subscribe(new EventObserver(this));

            // Reload fresh state from Asterisk
            states = new LoadConnectionStates(_connection);
            await LoadAsync(states, CancellationToken.None);
        }
        catch (AmiNotConnectedException ex)
        {
            // The reconnected session ended too before the reload completed. That is the connection ending again, not
            // a defect of the reload: a reconnect that follows reloads once more.
            VerbaraServerLog.ReloadInterrupted(_logger, states?.AtStop ?? _connection.State, ex.Message);
        }
        catch (Exception ex)
        {
            VerbaraServerLog.ReconnectReloadFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Request initial state snapshots from Asterisk.
    /// Sends StatusAction, QueueStatusAction, AgentsAction to populate managers.
    /// <para>
    /// The channel leg is read into a buffer first and reconciled only once that read completed —
    /// see <see cref="ReadChannelSnapshotAsync"/>. On a first load the table is empty and the
    /// reconciliation degenerates to "everything is added", which is exactly what this method did
    /// before; on a post-reconnect reload the difference is what drives the events.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// While Asterisk refuses the queues' or the agents' request because it is still loading its modules, the load
    /// asks again, as <see cref="StartAsync"/> describes.
    /// </para>
    /// <para>
    /// When the AMI session ends before the load completes, the load stops and throws, whether or not the connection
    /// is reconnecting: it sends nothing more, it reconciles no channel snapshot it did not finish reading, so it ends
    /// no call, and it never returns as if the state it holds were complete. The queues and agents it had read stay
    /// until the next load. <see cref="StartAsync"/>, by contrast, returns when the reconnect's reload will follow.
    /// </para>
    /// </remarks>
    /// <exception cref="AmiNotConnectedException">
    /// The connection is not established, or its AMI session ended before the load completed.
    /// </exception>
    public ValueTask RequestInitialStateAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(new LoadConnectionStates(_connection), cancellationToken);

    /// <summary>
    /// The load behind <see cref="RequestInitialStateAsync"/>, <see cref="StartAsync"/> and the reload after a
    /// reconnect. It records in <paramref name="states"/> the connection's state when it began and, when it stops
    /// because its AMI session ended, the state then: what the start reads to tell whether a reload will follow.
    /// </summary>
    private async ValueTask LoadAsync(LoadConnectionStates states, CancellationToken cancellationToken)
    {
        using var activity = LiveActivitySource.StartStateLoad(_connection.AsteriskVersion ?? "unknown");

        // Bound once, here: every decision this load takes about a starting Asterisk is about the session it began on.
        var session = _connection is AmiConnection ami ? new LoadSession(ami, TimeProvider) : null;

        // The request being sent, for the one ending the connection reports itself: a send it refuses.
        var request = "Status";
        try
        {
            // Populate channels from StatusAction. Buffer first, then reconcile: a snapshot that
            // throws, is cancelled or never completes must leave every held channel alone, and it can
            // only do that if nothing was mutated while it was being read (ADR-0062, design D1).
            var (admittedThrough, channelSnapshot) = await ReadChannelSnapshotAsync(session, states, cancellationToken);
            Channels.ReconcileWithSnapshot(channelSnapshot, admittedThrough);

            request = "QueueStatus";
            await LoadQueuesAsync(session, states, cancellationToken);

            request = "Agents";
            await PopulateAgentsAsync(session, states, cancellationToken);
        }
        catch (AmiNotConnectedException) when (states.AtStop is null && states.BeganOnASession)
        {
            // The connection refused a send because the session had ended: the third way a load learns of it, after
            // an action abandoned by its session and a FullyBooted cancelled by it. The load stops there, as it does
            // at the other two, and says so in the same words. A load that began on no session keeps today's
            // exception untouched, since it was started before the connection was established.
            throw states.Stop($"the live state load could not send {request}, because its AMI session had ended");
        }

        VerbaraServerLog.InitialStateLoaded(_logger, Channels.ChannelCount, Queues.QueueCount, Agents.AgentCount);
        LiveActivitySource.SetStateLoadResult(activity, Channels.ChannelCount, Queues.QueueCount, Agents.AgentCount);
    }

    private async ValueTask LoadQueuesAsync(
        LoadSession? session, LoadConnectionStates states, CancellationToken cancellationToken)
    {
        // Populate queues from QueueStatusAction
        await foreach (var evt in AskWhileAsteriskStartsAsync(
            "QueueStatus", static () => new QueueStatusAction(), session, states, cancellationToken))
        {
            switch (evt)
            {
                case QueueParamsEvent qp:
                    Queues.OnQueueParams(
                        qp.Queue ?? "", qp.Max ?? 0, qp.Strategy,
                        qp.Calls ?? 0, qp.HoldTime ?? 0, qp.TalkTime ?? 0,
                        qp.Completed ?? 0, qp.Abandoned ?? 0);
                    break;
                case QueueMemberEvent qm:
                    Queues.OnMemberAdded(
                        qm.Queue ?? "", qm.Location ?? qm.Interface ?? "", qm.MemberName,
                        qm.Penalty ?? 0, qm.Paused ?? false, qm.Status ?? 0);
                    break;
                case QueueEntryEvent qe:
                    // The entry is marked as a snapshot's, and carries the Wait Asterisk reported, so
                    // the session manager can tell a caller it already holds from a new one and date
                    // the visit from when Asterisk says the caller joined.
                    Queues.OnCallerJoined(
                        qe.Queue ?? "", qe.Channel ?? "", qe.CallerId, qe.Position ?? 0,
                        fromSnapshot: true, reportedWaitSeconds: qe.Wait);
                    break;
            }
        }
    }

    /// <summary>
    /// Read every channel Asterisk reports on a <c>Status</c> snapshot into a buffer, and return it
    /// only once the enumeration ran to completion.
    /// <para>
    /// Nothing is mutated while the snapshot is being read. That is the whole point: absence from an
    /// unfinished snapshot is not evidence that a channel is gone, and
    /// <see cref="ChannelManager.ReconcileWithSnapshot"/> removes exactly what the snapshot omits.
    /// A snapshot that throws or is cancelled therefore leaves the channel table — and every call
    /// session derived from it — untouched, because the buffer is discarded with the exception and
    /// the reconciliation never runs. <c>OnReconnected</c> is <c>async void</c> and swallows every
    /// exception into a log line, so a failure path that mutates nothing is the only one that stays
    /// safe underneath it (ADR-0062, design D1).
    /// </para>
    /// <para>
    /// The returned admission mark is read <c>before</c> the <c>Status</c> action is sent, and says
    /// how much of the channel table this snapshot could possibly describe. <c>OnReconnected</c>
    /// re-subscribes the event observer before it awaits this read, so a call that starts during the
    /// read is admitted live and is legitimately absent from the older snapshot; on a large estate
    /// that window is a full <c>Status</c> round trip. The mark is what keeps the reconciliation
    /// from reading that absence as a hangup and ending a call that is up (ADR-0062, design D6).
    /// </para>
    /// <para>
    /// A snapshot whose AMI session ended before <c>StatusComplete</c> is as unfinished as a cancelled one, and the
    /// connection ends it quietly, with the channels listed so far. Over an <see cref="AmiConnection"/>, which says so,
    /// it throws instead of returning that partial list; any other <see cref="IAmiConnection"/> is read as before.
    /// </para>
    /// </summary>
    private async ValueTask<(long AdmittedThrough, List<ChannelSnapshotEntry> Entries)>
        ReadChannelSnapshotAsync(LoadSession? session, LoadConnectionStates states, CancellationToken cancellationToken)
    {
        // Before the request, never after: a mark read once the answer is in hand would place every
        // channel that arrived meanwhile at or below it, and hand the reconciliation the power to
        // end those calls.
        var admittedThrough = Channels.CaptureAdmissionMark();

        var snapshot = new List<ChannelSnapshotEntry>();

        var outcome = session is null ? null : new EventActionOutcome();
        var events = session is null
            ? _connection.SendEventGeneratingActionAsync(new StatusAction(), cancellationToken)
            : session.Connection.SendEventGeneratingActionAsync(new StatusAction(), outcome, cancellationToken);

        await foreach (var evt in events)
        {
            if (evt is not StatusEvent se)
                continue;

            // Every field below that Asterisk really sends is read from RawFields. StatusEvent's
            // own State and CallerId properties are read by nothing here on purpose: no supported
            // version populates them, so the parse that used to consume them could only ever
            // produce ChannelState.Unknown and a null caller id (ADR-0062, design D5).
            var rawFields = se.RawFields;
            snapshot.Add(new ChannelSnapshotEntry(
                se.UniqueId ?? "",
                se.Channel ?? "",
                ReadChannelState(rawFields),
                // CallerIDNum / CallerIDName are the header names every channel-bearing event uses;
                // Status carries them too, and StatusEvent.CallerId — which is what this read
                // before — is not among the headers any measured version sends.
                CallerIdNum: rawFields?.GetValueOrDefault("CallerIDNum"),
                CallerIdName: rawFields?.GetValueOrDefault("CallerIDName"),
                // Context reaches the manager only through RawFields — StatusEvent has no typed
                // property for it — and CallSessionManager infers a call's direction from it.
                Context: rawFields?.GetValueOrDefault("Context"),
                Extension: se.Extension,
                // The correlation Asterisk already put on the wire. Dropping it is what turned one
                // call into several: a channel admitted with no LinkedId falls back to its own
                // UniqueId in CallSessionManager, so the two legs of a call that started during the
                // outage — neither of which the SDK ever saw — open two sessions instead of one.
                // Measured present, non-empty and identical across every leg of one call on 18.26.4,
                // 20.20.1, 22.9.0 and 23.4.1, so no mapping is needed: pass it (ADR-0062, design D4).
                // An absent or empty header still degrades to linkedId = uniqueId downstream, which
                // is exactly today's behaviour for an uncorrelated channel.
                LinkedId: se.LinkedId));
        }

        // An enumeration that honoured the token by stopping quietly rather than by throwing would
        // hand back a truncated snapshot that reads as complete, and reconciling that would remove
        // every channel the snapshot had not reached yet. Cancellation is not completion.
        cancellationToken.ThrowIfCancellationRequested();

        // Nor is the end of the session: reconciling what it cut short would end every call it had not listed yet.
        if (outcome is { SessionEnded: true })
            throw states.Stop("the AMI session ended while the live state load was reading Status");

        return (admittedThrough, snapshot);
    }

    /// <summary>
    /// Read a <c>Status</c> frame's channel state from the header Asterisk actually sends.
    /// <para>
    /// The <b>numeric</b> <c>ChannelState</c> is the one read, not the text <c>ChannelStateDesc</c>,
    /// even though both are on the wire on every measured version. <see cref="ChannelState"/>'s
    /// members map 1:1 onto Asterisk's numeric values, so the numeric header round-trips for all
    /// eleven states; the text spellings do not — Asterisk writes <c>Rsrvd</c>,
    /// <c>Dialing Offhook</c> and <c>Pre-ring</c> where this enum names
    /// <c>Reserved</c>, <c>DialingOffHook</c> and <c>PreRing</c>, and each of those three would
    /// silently land as <c>Unknown</c>. Reading the numeric header is therefore lossless where
    /// reading the text one would re-introduce a quieter version of the defect D5 removes.
    /// </para>
    /// <para>
    /// A channel whose frame carries no state header at all still defaults to
    /// <see cref="ChannelState.Unknown"/> and is still admitted: defaulting is correct when the
    /// value is genuinely absent. What was wrong was defaulting a value that could never arrive.
    /// </para>
    /// </summary>
    private static ChannelState ReadChannelState(IReadOnlyDictionary<string, string>? rawFields)
    {
        if (rawFields is null || !rawFields.TryGetValue("ChannelState", out var numeric))
            return ChannelState.Unknown;

        // The enum's values are contiguous from Down = 0 to Unknown = 10, so a value inside those
        // bounds is a named member. Anything else — a state a future Asterisk adds, or a header
        // that is not a number — is genuinely unknown to this SDK and says so.
        return int.TryParse(numeric, CultureInfo.InvariantCulture, out var value)
            && value is >= (int)ChannelState.Down and <= (int)ChannelState.Unknown
                ? (ChannelState)value
                : ChannelState.Unknown;
    }

    private async ValueTask PopulateAgentsAsync(
        LoadSession? session, LoadConnectionStates states, CancellationToken cancellationToken)
    {
        await foreach (var evt in AskWhileAsteriskStartsAsync(
            "Agents", static () => new AgentsAction(), session, states, cancellationToken))
        {
            if (evt is not AgentsEvent ae || ae.Agent is null)
                continue;

            var isLoggedOff = string.Equals(ae.Status, "AGENT_LOGGEDOFF", StringComparison.OrdinalIgnoreCase);
            if (isLoggedOff)
            {
                Agents.RegisterAgent(ae.Agent, ae.Name);
                continue;
            }

            Agents.OnAgentLogin(ae.Agent, ae.LoggedInChan);
            if (ae.Name is not null)
                Agents.GetById(ae.Agent)?.SetName(ae.Name);
        }
    }

    /// <summary>
    /// Sends one of a load's requests and yields its events, asking again while Asterisk refuses it because it is
    /// still starting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asterisk accepts an AMI login before its modules have loaded. Until app_queue and app_agent_pool have registered
    /// <c>QueueStatus</c> and <c>Agents</c>, it refuses them as an unknown command, which is also what it answers for
    /// good when the module is not loaded at all. A refusal carries no events, so taken as it comes it reads like an
    /// empty table: a load in that window would record no queues and no agents, and nothing would bring them back
    /// before the next reconnect. The decision uses only the two signals Asterisk gives, the refusal and
    /// <c>FullyBooted</c> on the load's session:
    /// </para>
    /// <list type="bullet">
    /// <item><description>an answer that is not a refusal as an unknown command ends the request, as it always
    /// did;</description></item>
    /// <item><description>a refusal as an unknown command when <c>FullyBooted</c> had arrived before the ask is final: the
    /// module is not loaded, and the empty answer is true;</description></item>
    /// <item><description>one before it is asked again once <c>FullyBooted</c> arrives, or after
    /// <see cref="NotRegisteredRetryInterval"/> for a user who never receives it;</description></item>
    /// <item><description>once <see cref="NotRegisteredRetryBudget"/> has passed since the load's first such refusal,
    /// the request ends without the refused state, and a warning names it.</description></item>
    /// </list>
    /// <para>
    /// When the session ends, while the request is pending or while the load waits between two asks, it throws and
    /// asks nothing more on it.
    /// </para>
    /// <para>
    /// Only an <see cref="AmiConnection"/> says how an action ended and when its session reported <c>FullyBooted</c>;
    /// any other <see cref="IAmiConnection"/> is asked once, as before.
    /// </para>
    /// </remarks>
    /// <param name="actionName">The request's AMI action name, for the log.</param>
    /// <param name="newAction">Creates the request. Each ask sends a new one, because an action keeps the
    /// <c>ActionID</c> it was first sent with.</param>
    /// <param name="session">The session the load began on, or <see langword="null"/> for a connection that cannot
    /// tell a refusal from an empty answer.</param>
    /// <param name="states">The load's record of the connection's state, which an ending of the session completes.</param>
    /// <param name="cancellationToken">Cancels the request and any wait between two asks.</param>
    private async IAsyncEnumerable<ManagerEvent> AskWhileAsteriskStartsAsync(
        string actionName, Func<ManagerAction> newAction, LoadSession? session, LoadConnectionStates states,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (session is null)
        {
            await foreach (var evt in _connection.SendEventGeneratingActionAsync(newAction(), cancellationToken))
                yield return evt;

            yield break;
        }

        var toldWaiting = false;
        while (true)
        {
            // Read before the ask, never once its answer is in: a report that lands between the two says nothing about
            // the module at the moment it was asked, and taking that refusal as final would drop a module still loading.
            var reportedBeforeAsk = session.FullyBooted.IsCompletedSuccessfully;

            var outcome = new EventActionOutcome();
            await foreach (var evt in session.Connection.SendEventGeneratingActionAsync(newAction(), outcome, cancellationToken))
                yield return evt;

            if (outcome.SessionEnded)
                throw states.Stop($"the AMI session ended while the live state load was reading {actionName}");

            if (outcome.Rejection is not { } rejection
                || reportedBeforeAsk
                || !rejection.StartsWith(UnknownCommandPrefix, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            if (session.BudgetSpent())
            {
                VerbaraServerLog.ActionNeverRegistered(
                    _logger, actionName, (int)NotRegisteredRetryBudget.TotalSeconds, rejection);
                yield break;
            }

            if (!toldWaiting)
            {
                VerbaraServerLog.ActionNotRegisteredYet(
                    _logger, actionName, (int)NotRegisteredRetryInterval.TotalMilliseconds);
                toldWaiting = true;
            }

            // The session ended while the load waited for it: nothing more is asked on it.
            if (!await session.WaitForFullyBootedAsync(cancellationToken))
            {
                throw states.Stop(
                    $"the AMI session ended while the live state load waited for FullyBooted to ask {actionName} again");
            }
        }
    }

    /// <summary>
    /// The AMI session a load began on, as that load sees it: the session's <c>FullyBooted</c>, and the load's one budget
    /// for asking a starting Asterisk again.
    /// </summary>
    private sealed class LoadSession(AmiConnection connection, TimeProvider clock)
    {
        // The timestamp of the load's first refusal from a starting Asterisk, on the clock's monotonic timestamp.
        private long? _firstRefusalAt;

        public AmiConnection Connection { get; } = connection;

        /// <summary>
        /// The session's report, read once, when the load begins. A reconnect gives the connection a new task, and a
        /// report made on another session says nothing about the requests this load makes on its own.
        /// </summary>
        public Task FullyBooted { get; } = connection.FullyBooted;

        /// <summary>
        /// Whether the load's budget is spent. The first call starts it, at the load's first refusal from a starting
        /// Asterisk, whichever request that was; every later refusal of the same load reads the same budget.
        /// </summary>
        public bool BudgetSpent()
        {
            _firstRefusalAt ??= clock.GetTimestamp();
            return clock.GetElapsedTime(_firstRefusalAt.Value) >= NotRegisteredRetryBudget;
        }

        /// <summary>
        /// Waits for the session's <c>FullyBooted</c> or for <see cref="NotRegisteredRetryInterval"/>, whichever comes
        /// first. Returns <see langword="false"/> when the session ended during the wait. The interval's timer is
        /// cancelled when the report wins, so no timer outlives the wait.
        /// </summary>
        public async Task<bool> WaitForFullyBootedAsync(CancellationToken cancellationToken)
        {
            using var intervalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var interval = Task.Delay(NotRegisteredRetryInterval, clock, intervalCts.Token);
            if (await Task.WhenAny(FullyBooted, interval) != interval)
                await intervalCts.CancelAsync();

            cancellationToken.ThrowIfCancellationRequested();
            return !FullyBooted.IsCanceled;
        }
    }

    /// <summary>
    /// The connection's state as one load read it: when the load began, and when the load stopped because its AMI
    /// session ended. The second read is the one that tells whether the connection will come back, because the
    /// connection writes the state its ending chose before it wakes anything waiting on the session.
    /// </summary>
    private sealed class LoadConnectionStates(IAmiConnection connection)
    {
        /// <summary>The state when the load began.</summary>
        public AmiConnectionState AtStart { get; } = connection.State;

        /// <summary>The state when the load stopped because its session ended; <see langword="null"/> until then.</summary>
        public AmiConnectionState? AtStop { get; private set; }

        /// <summary>
        /// Whether the load began on a session, or while the reconnect loop was replacing one that had ended. From any
        /// other state the connection was not established, and the load's first request fails as it always did.
        /// </summary>
        public bool BeganOnASession => AtStart is AmiConnectionState.Connected or AmiConnectionState.Reconnecting;

        /// <summary>
        /// Whether a reload will follow a load that stopped because its session ended: the connection was reconnecting
        /// when the load began, or it was connected then and the reconnect loop owned it when the load stopped.
        /// <c>Disconnecting</c> and <c>Disconnected</c> mean that automatic reconnection is off, that the reconnect gave
        /// up, or that the caller ended the connection. A load that began in any other state reads as never connected,
        /// including one that began in <c>Connecting</c>: the state cannot tell a caller's first connect still running
        /// from the reconnect loop's attempt, and a caller's connect raises no reload.
        /// </summary>
        public bool ReconnectWillReload => AtStop is { } stop
            && (AtStart == AmiConnectionState.Reconnecting
                || (AtStart == AmiConnectionState.Connected
                    && stop is AmiConnectionState.Reconnecting or AmiConnectionState.Connecting
                        or AmiConnectionState.Connected));

        /// <summary>
        /// Records the state now, as the one the load stopped in, and returns the exception the load stops with: the
        /// connection's own not-connected exception, whose message says what the load was doing and the state.
        /// </summary>
        public AmiNotConnectedException Stop(string what)
        {
            var state = connection.State;
            AtStop = state;
            return new AmiNotConnectedException($"Not connected: {what}. Current state: {state}");
        }
    }

    /// <summary>
    /// Originate an outbound call asynchronously.
    /// </summary>
    public async ValueTask<OriginateResult> OriginateAsync(
        string channel, string context, string extension, int priority = 1,
        string? callerId = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = LiveActivitySource.StartOriginate(channel, context, extension);

        var action = new OriginateAction
        {
            Channel = channel,
            Context = context,
            Exten = extension,
            Priority = priority,
            CallerId = callerId,
            Timeout = timeout.HasValue ? (long)timeout.Value.TotalMilliseconds : 30000,
            IsAsync = true
        };

        await foreach (var evt in _connection.SendEventGeneratingActionAsync(action, cancellationToken))
        {
            if (evt is OriginateResponseEvent ore)
            {
                var success = string.Equals(ore.Response, "Success", StringComparison.OrdinalIgnoreCase);
                LiveActivitySource.SetOriginateResult(activity, success, ore.Response);
                return new OriginateResult
                {
                    Success = success,
                    Message = ore.Response,
                    ChannelId = ore.Channel
                };
            }
        }

        LiveActivitySource.SetOriginateResult(activity, false, "No OriginateResponse received");
        return new OriginateResult { Success = false, Message = "No OriginateResponse received" };
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Reconnected -= OnReconnected;
        if (_connection is AmiConnection amiConnection)
            amiConnection.Lost -= OnConnectionLost;
        _subscription?.Dispose();
        if (_ariClient is not null)
            await _ariClient.DisposeAsync();
        _instanceMeter.Dispose();
    }

    private void OnConnectionLost(Exception? cause)
    {
        VerbaraServerLog.AmiConnectionLost(_logger, cause);
        RaiseConnectionLost(cause);
    }

    /// <summary>Raises <see cref="ConnectionLost"/> handler by handler, so that one that throws does not starve the rest.</summary>
    private void RaiseConnectionLost(Exception? cause)
    {
        var handlers = ConnectionLost;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<Exception?>)handler).Invoke(cause);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A subscriber's handler is foreign code: whatever it throws is logged, so that the handlers after it
                // are still told of the loss. Running out of memory is the process's failure, not the handler's, so it
                // is not swallowed here.
                VerbaraServerLog.ConnectionLostHandlerError(_logger, ex);
            }
        }
    }

    /// <summary>Internal observer that dispatches typed AMI events to the appropriate manager.</summary>
    private sealed class EventObserver(VerbaraServer server) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                // Channel events — typed via source-generated deserializer
                case NewChannelEvent nce:
                    server.Channels.OnNewChannel(
                        nce.UniqueId ?? "",
                        nce.Channel ?? "",
                        Enum.TryParse<ChannelState>(nce.ChannelState, out var cs) ? cs : ChannelState.Unknown,
                        nce.CallerIdNum,
                        nce.CallerIdName,
                        nce.Context,
                        nce.Exten,
                        nce.Priority ?? 1,
                        nce.Linkedid);
                    break;

                case NewStateEvent nse:
                    server.Channels.OnNewState(
                        nse.UniqueId ?? "",
                        Enum.TryParse<ChannelState>(nse.ChannelState, out var ns) ? ns : ChannelState.Unknown);
                    break;

                case HangupEvent he:
                    server.Channels.OnHangup(
                        he.UniqueId ?? "",
                        he.Cause is not null && Enum.IsDefined(typeof(HangupCause), he.Cause.Value)
                            ? (HangupCause)he.Cause.Value
                            : HangupCause.NormalClearing);
                    break;

                case RenameEvent re:
                    server.Channels.OnRename(
                        re.UniqueId ?? "",
                        re.RawFields?.GetValueOrDefault("Newname") ?? "");
                    break;

                // Queue events
                case QueueMemberAddedEvent qma:
                    server.Queues.OnMemberAdded(
                        qma.Queue ?? "",
                        qma.Location ?? qma.Interface ?? "",
                        qma.MemberName,
                        qma.Penalty ?? 0,
                        qma.Paused ?? false,
                        qma.Status ?? 0);
                    break;

                case QueueMemberRemovedEvent qmr:
                    server.Queues.OnMemberRemoved(
                        qmr.Queue ?? "",
                        qmr.Location ?? qmr.Interface ?? "");
                    break;

                case QueueMemberPausedEvent qmp:
                    server.Queues.OnMemberPaused(
                        qmp.Queue ?? "",
                        qmp.Location ?? qmp.Interface ?? "",
                        qmp.Paused ?? false,
                        qmp.Reason);
                    break;

                case QueueMemberStatusEvent qms:
                    server.Queues.OnMemberStatusChanged(
                        qms.Queue ?? "",
                        qms.Location ?? qms.Interface ?? "",
                        qms.Status ?? 0);
                    break;

                case QueueMemberPauseEvent qmpe:
                    server.Queues.OnMemberPaused(
                        qmpe.RawFields?.GetValueOrDefault("Queue") ?? "",
                        qmpe.RawFields?.GetValueOrDefault("Location") ?? qmpe.RawFields?.GetValueOrDefault("Interface") ?? "",
                        qmpe.RawFields?.GetValueOrDefault("Paused") == "1",
                        qmpe.Pausedreason);
                    break;

                case QueueCallerJoinEvent qcj:
                    server.Queues.OnCallerJoined(
                        qcj.RawFields?.GetValueOrDefault("Queue") ?? "",
                        qcj.RawFields?.GetValueOrDefault("Channel") ?? "",
                        qcj.RawFields?.GetValueOrDefault("CallerIDNum"),
                        qcj.Position ?? 0);
                    break;

                case QueueCallerLeaveEvent qcl:
                    server.Queues.OnCallerLeft(
                        qcl.RawFields?.GetValueOrDefault("Queue") ?? "",
                        qcl.RawFields?.GetValueOrDefault("Channel") ?? "");
                    break;

                // Agent events
                case AgentLoginEvent ale:
                    server.Agents.OnAgentLogin(ale.Agent ?? "", ale.Channel);
                    break;

                case AgentLogoffEvent alo:
                    server.Agents.OnAgentLogoff(alo.Agent ?? "");
                    break;

                case AgentConnectEvent ace:
                    server.Agents.OnAgentConnect(ace.Agent ?? "", ace.Channel,
                        ace.LinkedId, ace.Interface);
                    // app_queue's own report that it connected the caller, agent or not. Raised
                    // after OnAgentConnect so a known agent's handlers run first, and keyed by the
                    // caller's Uniqueid, which a Linkedid rewrite does not move.
                    server.Agents.OnQueueCallerConnected(ace.UniqueId,
                        ace.RawFields?.GetValueOrDefault("MemberName"), ace.Interface);
                    break;

                case AgentCompleteEvent acoe:
                    server.Agents.OnAgentComplete(
                        acoe.Agent ?? "",
                        acoe.TalkTime ?? 0,
                        acoe.HoldTime ?? 0);
                    break;

                // Device state events
                case DeviceStateChangeEvent dsc:
                    if (dsc.Device is not null && dsc.State is not null)
                        server.Queues.OnDeviceStateChanged(dsc.Device, dsc.State);
                    break;

                // MeetMe/ConfBridge events
#pragma warning disable CS0618 // MeetMe events still received from Asterisk 18-20
                case MeetMeJoinEvent mmj:
                    server.MeetMe.OnUserJoined(mmj.Meetme ?? "", mmj.Usernum ?? 0, mmj.Channel ?? "");
                    break;

                case ConfbridgeJoinEvent cbj:
                    server.MeetMe.OnUserJoined(cbj.Conference ?? "", 0, cbj.Channel ?? "");
                    break;

                case MeetMeLeaveEvent mml:
                    server.MeetMe.OnUserLeft(mml.Meetme ?? "", mml.Usernum ?? 0);
                    break;
#pragma warning restore CS0618

                case ConfbridgeLeaveEvent cbl:
                    server.MeetMe.OnUserLeft(cbl.Conference ?? "", 0);
                    break;

                // Bridge events
                case BridgeCreateEvent bce:
                    server.Bridges.OnBridgeCreated(
                        bce.BridgeUniqueid!,
                        bce.BridgeType,
                        bce.BridgeTechnology,
                        bce.BridgeCreator,
                        bce.BridgeName);
                    break;

                case BridgeEnterEvent bee:
                    server.Bridges.OnChannelEntered(bee.BridgeUniqueid!, bee.UniqueId!);
                    var enterBridge = server.Bridges.GetById(bee.BridgeUniqueid!);
                    if (enterBridge is not null && enterBridge.NumChannels == 2)
                    {
                        var otherUid = enterBridge.Channels.Keys.FirstOrDefault(k => k != bee.UniqueId);
                        if (otherUid is not null)
                            server.Channels.OnLink(bee.UniqueId!, otherUid);
                    }
                    break;

                case BridgeLeaveEvent ble:
                    var leaveBridge = server.Bridges.GetById(ble.BridgeUniqueid!);
                    if (leaveBridge is not null)
                    {
                        var otherUid2 = leaveBridge.Channels.Keys.FirstOrDefault(k => k != ble.UniqueId);
                        if (otherUid2 is not null)
                            server.Channels.OnUnlink(ble.UniqueId!, otherUid2);
                    }
                    server.Bridges.OnChannelLeft(ble.BridgeUniqueid!, ble.UniqueId!);
                    break;

                case BridgeDestroyEvent bde:
                    server.Bridges.OnBridgeDestroyed(bde.BridgeUniqueid!);
                    break;

                // Dial events. For an AMI Originate, Asterisk's DialBegin and DialEnd name only the
                // dialed side (DestChannel/DestUniqueid) and carry no Uniqueid: there is no calling
                // channel to attribute the dial to, so the event is skipped. It is the ordinary shape
                // of every originate, not an anomaly, so it is neither logged nor counted.
                case DialBeginEvent dbe:
                    if (dbe.UniqueId is null)
                        break;
                    server.Channels.OnDialBegin(
                        dbe.UniqueId,
                        dbe.DestUniqueid!,
                        dbe.DestChannel!,
                        dbe.DialString);
                    break;

                case DialEndEvent dee:
                    if (dee.UniqueId is null)
                        break;
                    server.Channels.OnDialEnd(dee.UniqueId, dee.DialStatus);
                    break;

                // Hold events
                case HoldEvent hoe:
                    server.Channels.OnHold(hoe.UniqueId!, hoe.MusicClass);
                    break;

                case UnholdEvent uhe:
                    server.Channels.OnUnhold(uhe.UniqueId!);
                    break;

                // Transfer events
                case BlindTransferEvent bte:
                    server.Bridges.OnBlindTransfer(
                        bte.BridgeUniqueid!,
                        bte.TransfereeChannel,
                        bte.Extension,
                        bte.TransfereeContext);
                    break;

                case AttendedTransferEvent ate:
                    server.Bridges.OnAttendedTransfer(
                        ate.OrigBridgeUniqueid!,
                        ate.SecondBridgeUniqueid,
                        ate.DestType,
                        ate.Result);
                    break;
            }
        }

        public void OnError(Exception error)
        {
            VerbaraServerLog.ConnectionError(server._logger, error);
            server.RaiseConnectionLost(error);
        }

        public void OnCompleted()
        {
            VerbaraServerLog.ConnectionClosed(server._logger);
            server.RaiseConnectionLost(null);
        }
    }
}

/// <summary>Result of an originate operation.</summary>
public sealed class OriginateResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? ChannelId { get; init; }
}
