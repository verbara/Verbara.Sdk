using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Enums;
using Asterisk.NetAot.Ami.Actions;
using Asterisk.NetAot.Ami.Events;
using Asterisk.NetAot.Ami.Events.Base;
using Asterisk.NetAot.Live.Agents;
using Asterisk.NetAot.Live.Channels;
using Asterisk.NetAot.Live.MeetMe;
using Asterisk.NetAot.Live.Queues;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Server;

internal static partial class AsteriskServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Initial state loaded: {Channels} channels, {Queues} queues, {Agents} agents")]
    public static partial void InitialStateLoaded(ILogger logger, int channels, int queues, int agents);
}

/// <summary>
/// Aggregate root for real-time Asterisk state tracking.
/// Listens to AMI events and maintains live domain objects:
/// channels, queues, agents, and conference rooms.
/// </summary>
public sealed class AsteriskServer : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly ILogger<AsteriskServer> _logger;
    private IDisposable? _subscription;

    public ChannelManager Channels { get; }
    public QueueManager Queues { get; }
    public AgentManager Agents { get; }
    public MeetMeManager MeetMe { get; }

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
    }

    /// <summary>
    /// Initialize state by subscribing to AMI events.
    /// Call this after the AMI connection is established.
    /// </summary>
    public void StartTracking()
    {
        _subscription = _connection.Subscribe(new EventObserver(this));
    }

    /// <summary>
    /// Request initial state snapshots from Asterisk.
    /// Sends StatusAction, QueueStatusAction, AgentsAction to populate managers.
    /// </summary>
    public async ValueTask RequestInitialStateAsync(CancellationToken cancellationToken = default)
    {
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
                        qm.Queue ?? "", qm.Interface ?? "", qm.MemberName,
                        qm.Penalty ?? 0, qm.Paused ?? false, qm.Status ?? 0);
                    break;
                case QueueEntryEvent qe:
                    Queues.OnCallerJoined(
                        qe.Queue ?? "", qe.Channel ?? "", qe.CallerId, qe.Position ?? 0);
                    break;
            }
        }

        // Populate agents from AgentsAction
        await foreach (var evt in _connection.SendEventGeneratingActionAsync(new AgentsAction(), cancellationToken))
        {
            if (evt is AgentsEvent ae && ae.Agent is not null)
            {
                if (!string.Equals(ae.Status, "AGENT_LOGGEDOFF", StringComparison.OrdinalIgnoreCase))
                {
                    Agents.OnAgentLogin(ae.Agent, ae.LoggedInChan);
                }
            }
        }

        AsteriskServerLog.InitialStateLoaded(_logger, Channels.ActiveChannels.Count, Queues.Queues.Count, Agents.Agents.Count);
    }

    /// <summary>
    /// Originate an outbound call asynchronously.
    /// </summary>
    public async ValueTask<OriginateResult> OriginateAsync(
        string channel, string context, string extension, int priority = 1,
        string? callerId = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var action = new OriginateAction
        {
            Channel = channel,
            Context = context,
            Exten = extension,
            Priority = priority,
            CallerId = callerId,
            Timeout = timeout.HasValue ? (long)timeout.Value.TotalMilliseconds : 30000,
            Async = true
        };

        await foreach (var evt in _connection.SendEventGeneratingActionAsync(action, cancellationToken))
        {
            if (evt is OriginateResponseEvent ore)
            {
                var success = string.Equals(ore.Response, "Success", StringComparison.OrdinalIgnoreCase);
                return new OriginateResult
                {
                    Success = success,
                    Message = ore.Response,
                    ChannelId = ore.Channel
                };
            }
        }

        return new OriginateResult { Success = false, Message = "No OriginateResponse received" };
    }

    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return ValueTask.CompletedTask;
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
                        nce.Priority ?? 1);
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
                        qma.Interface ?? "",
                        qma.MemberName,
                        qma.Penalty ?? 0,
                        qma.Paused ?? false,
                        qma.Status ?? 0);
                    break;

                case QueueMemberRemovedEvent qmr:
                    server.Queues.OnMemberRemoved(
                        qmr.Queue ?? "",
                        qmr.Interface ?? "");
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
                    server.Agents.OnAgentConnect(ace.Agent ?? "", ace.Channel);
                    break;

                case AgentCompleteEvent acoe:
                    server.Agents.OnAgentComplete(acoe.Agent ?? "");
                    break;

                // MeetMe/ConfBridge events
                case MeetMeJoinEvent mmj:
                    server.MeetMe.OnUserJoined(mmj.Meetme ?? "", mmj.Usernum ?? 0, mmj.Channel ?? "");
                    break;

                case ConfbridgeJoinEvent cbj:
                    server.MeetMe.OnUserJoined(cbj.Conference ?? "", 0, cbj.Channel ?? "");
                    break;

                case MeetMeLeaveEvent mml:
                    server.MeetMe.OnUserLeft(mml.Meetme ?? "", mml.Usernum ?? 0);
                    break;

                case ConfbridgeLeaveEvent cbl:
                    server.MeetMe.OnUserLeft(cbl.Conference ?? "", 0);
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>Result of an originate operation.</summary>
public sealed class OriginateResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? ChannelId { get; init; }
}
