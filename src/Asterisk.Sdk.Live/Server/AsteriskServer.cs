using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Events.Base;
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Bridges;
using Asterisk.Sdk.Live.Channels;
using Asterisk.Sdk.Live.Diagnostics;
using Asterisk.Sdk.Live.MeetMe;
using Asterisk.Sdk.Live.Queues;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Live.Server;

internal static partial class AsteriskServerLog
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
}

/// <summary>
/// Aggregate root for real-time Asterisk state tracking.
/// Listens to AMI events and maintains live domain objects:
/// channels, queues, agents, and conference rooms.
/// </summary>
public sealed class AsteriskServer : IAsteriskServer
{
    private readonly IAmiConnection _connection;
    private readonly ILogger<AsteriskServer> _logger;
    private IDisposable? _subscription;
    private IAriClient? _ariClient;

    public ChannelManager Channels { get; }
    public QueueManager Queues { get; }
    public AgentManager Agents { get; }
    public MeetMeManager MeetMe { get; }
    public BridgeManager Bridges { get; }

    /// <summary>Fired when the AMI connection is lost or completed.</summary>
    public event Action<Exception?>? ConnectionLost;

    /// <summary>The underlying AMI connection for this server.</summary>
    public IAmiConnection Connection => _connection;

    /// <summary>The optional ARI client for this server.</summary>
    public IAriClient? AriClient => _ariClient;

    /// <summary>The Asterisk version string.</summary>
    public string? AsteriskVersion => _connection.AsteriskVersion;

    public AsteriskServer(IAmiConnection connection, ILogger<AsteriskServer> logger)
    {
        _connection = connection;
        _logger = logger;
        Channels = new ChannelManager(logger);
        Queues = new QueueManager(logger);
        Agents = new AgentManager(logger);
        MeetMe = new MeetMeManager(logger);
        Bridges = new BridgeManager(logger);
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
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription = _connection.Subscribe(new EventObserver(this));
        _connection.Reconnected += OnReconnected;

        // Register observable gauges for live state
        LiveMetrics.Meter.CreateObservableGauge("live.channels.active",
            () => Channels.ChannelCount, description: "Active channels");
        LiveMetrics.Meter.CreateObservableGauge("live.queues.count",
            () => Queues.QueueCount, description: "Configured queues");
        LiveMetrics.Meter.CreateObservableGauge("live.agents.total",
            () => Agents.AgentCount, description: "Total tracked agents");
        LiveMetrics.Meter.CreateObservableGauge("live.agents.available",
            () => Agents.Agents.Count(a => a.State == AgentState.Available),
            description: "Agents in Available state");
        LiveMetrics.Meter.CreateObservableGauge("live.agents.on_call",
            () => Agents.Agents.Count(a => a.State == AgentState.OnCall),
            description: "Agents currently on a call");
        LiveMetrics.Meter.CreateObservableGauge("live.agents.paused",
            () => Agents.Agents.Count(a => a.State == AgentState.Paused),
            description: "Agents in Paused state");
        LiveMetrics.Meter.CreateObservableGauge("live.agents.total_hold_secs",
            () => Agents.Agents.Sum(a => a.TotalHoldTimeSecs),
            unit: "s", description: "Aggregate hold time across all agents since login");
        LiveMetrics.Meter.CreateObservableGauge("live.agents.total_talk_secs",
            () => Agents.Agents.Sum(a => a.TotalTalkTimeSecs),
            unit: "s", description: "Aggregate talk time across all agents since login");

        await RequestInitialStateAsync(cancellationToken);
    }

    // Justification: event handler for Action delegate requires async void.
    // All exceptions are caught in the try/catch below — no unobserved exceptions.
#pragma warning disable VSTHRD100 // Avoid async void — required by Action event delegate
    private async void OnReconnected()
#pragma warning restore VSTHRD100
    {
        try
        {
            AsteriskServerLog.Reconnected(_logger);

            // Clear stale state from all managers
            Channels.Clear();
            Queues.Clear();
            Agents.Clear();
            MeetMe.Clear();
            Bridges.Clear();

            // Re-subscribe observer (the connection is new after reconnect)
            _subscription?.Dispose();
            _subscription = _connection.Subscribe(new EventObserver(this));

            // Reload fresh state from Asterisk
            await RequestInitialStateAsync();
        }
        catch (Exception ex)
        {
            AsteriskServerLog.ReconnectReloadFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Request initial state snapshots from Asterisk.
    /// Sends StatusAction, QueueStatusAction, AgentsAction to populate managers.
    /// </summary>
    public async ValueTask RequestInitialStateAsync(CancellationToken cancellationToken = default)
    {
        using var activity = LiveActivitySource.StartStateLoad(_connection.AsteriskVersion ?? "unknown");

        // Populate channels from StatusAction
        await foreach (var evt in _connection.SendEventGeneratingActionAsync(new StatusAction(), cancellationToken))
        {
            if (evt is StatusEvent se)
            {
                var state = Enum.TryParse<ChannelState>(se.State, out var cs) ? cs : ChannelState.Unknown;
                Channels.OnNewChannel(
                    se.UniqueId ?? "",
                    se.Channel ?? "",
                    state,
                    se.CallerId,
                    context: se.RawFields?.GetValueOrDefault("Context"),
                    exten: se.Extension);
            }
        }

        // Populate queues from QueueStatusAction
        await foreach (var evt in _connection.SendEventGeneratingActionAsync(new QueueStatusAction(), cancellationToken))
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
                    Queues.OnCallerJoined(
                        qe.Queue ?? "", qe.Channel ?? "", qe.CallerId, qe.Position ?? 0);
                    break;
            }
        }

        await PopulateAgentsAsync(cancellationToken);

        AsteriskServerLog.InitialStateLoaded(_logger, Channels.ChannelCount, Queues.QueueCount, Agents.AgentCount);
        LiveActivitySource.SetStateLoadResult(activity, Channels.ChannelCount, Queues.QueueCount, Agents.AgentCount);
    }

    private async ValueTask PopulateAgentsAsync(CancellationToken cancellationToken)
    {
        await foreach (var evt in _connection.SendEventGeneratingActionAsync(new AgentsAction(), cancellationToken))
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
        _subscription?.Dispose();
        if (_ariClient is not null)
            await _ariClient.DisposeAsync();
    }

    /// <summary>Internal observer that dispatches typed AMI events to the appropriate manager.</summary>
    private sealed class EventObserver(AsteriskServer server) : IObserver<ManagerEvent>
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

                // Dial events
                case DialBeginEvent dbe:
                    server.Channels.OnDialBegin(
                        dbe.UniqueId!,
                        dbe.DestUniqueid!,
                        dbe.DestChannel!,
                        dbe.DialString);
                    break;

                case DialEndEvent dee:
                    server.Channels.OnDialEnd(dee.UniqueId!, dee.DialStatus);
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
            AsteriskServerLog.ConnectionError(server._logger, error);
            server.ConnectionLost?.Invoke(error);
        }

        public void OnCompleted()
        {
            AsteriskServerLog.ConnectionClosed(server._logger);
            server.ConnectionLost?.Invoke(null);
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
