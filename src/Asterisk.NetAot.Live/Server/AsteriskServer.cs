using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Enums;
using Asterisk.NetAot.Live.Agents;
using Asterisk.NetAot.Live.Channels;
using Asterisk.NetAot.Live.MeetMe;
using Asterisk.NetAot.Live.Queues;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Server;

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
    #pragma warning disable CA1822 // Will use instance data when fully implemented
    public async ValueTask RequestInitialStateAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Send StatusAction -> receive StatusEvent per channel
        // TODO: Send QueueStatusAction -> receive QueueParams/QueueMember/QueueEntry events
        // TODO: Send AgentsAction -> receive AgentsEvent per agent
        // These require the event-generating action flow from AmiConnection
    }
    #pragma warning restore CA1822

    /// <summary>
    /// Originate an outbound call asynchronously.
    /// </summary>
    #pragma warning disable CA1822 // Will use instance data when fully implemented
    public async ValueTask<OriginateResult> OriginateAsync(
        string channel, string context, string extension, int priority = 1,
        string? callerId = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = channel; _ = context; _ = extension; _ = priority; _ = callerId; _ = timeout;
        await Task.CompletedTask;
        // TODO: Send OriginateAction via _connection with Async=true
        // TODO: Wait for OriginateResponseEvent correlation by ActionId
        return new OriginateResult { Success = false, Message = "Not yet implemented" };
    }
    #pragma warning restore CA1822

    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Internal observer that dispatches AMI events to the appropriate manager.</summary>
    private sealed class EventObserver(AsteriskServer server) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            var eventType = value.EventType;

            switch (eventType)
            {
                // Channel events
                case "Newchannel":
                    server.Channels.OnNewChannel(
                        value.UniqueId ?? "",
                        value.RawFields?.GetValueOrDefault("Channel") ?? "",
                        Enum.TryParse<ChannelState>(value.RawFields?.GetValueOrDefault("ChannelState"), out var cs) ? cs : ChannelState.Unknown,
                        value.RawFields?.GetValueOrDefault("CallerIDNum"),
                        value.RawFields?.GetValueOrDefault("CallerIDName"),
                        value.RawFields?.GetValueOrDefault("Context"),
                        value.RawFields?.GetValueOrDefault("Exten"),
                        int.TryParse(value.RawFields?.GetValueOrDefault("Priority"), out var p) ? p : 1);
                    break;

                case "Newstate":
                    server.Channels.OnNewState(
                        value.UniqueId ?? "",
                        Enum.TryParse<ChannelState>(value.RawFields?.GetValueOrDefault("ChannelState"), out var ns) ? ns : ChannelState.Unknown);
                    break;

                case "Hangup":
                    server.Channels.OnHangup(
                        value.UniqueId ?? "",
                        Enum.TryParse<HangupCause>(value.RawFields?.GetValueOrDefault("Cause"), out var hc) ? hc : HangupCause.NormalClearing);
                    break;

                case "Rename":
                    server.Channels.OnRename(
                        value.UniqueId ?? "",
                        value.RawFields?.GetValueOrDefault("Newname") ?? "");
                    break;

                // Queue events
                case "QueueMemberAdded":
                    server.Queues.OnMemberAdded(
                        value.RawFields?.GetValueOrDefault("Queue") ?? "",
                        value.RawFields?.GetValueOrDefault("Interface") ?? "",
                        value.RawFields?.GetValueOrDefault("MemberName"),
                        int.TryParse(value.RawFields?.GetValueOrDefault("Penalty"), out var pen) ? pen : 0,
                        string.Equals(value.RawFields?.GetValueOrDefault("Paused"), "1", StringComparison.Ordinal),
                        int.TryParse(value.RawFields?.GetValueOrDefault("Status"), out var st) ? st : 0);
                    break;

                case "QueueMemberRemoved":
                    server.Queues.OnMemberRemoved(
                        value.RawFields?.GetValueOrDefault("Queue") ?? "",
                        value.RawFields?.GetValueOrDefault("Interface") ?? "");
                    break;

                case "QueueCallerJoin":
                    server.Queues.OnCallerJoined(
                        value.RawFields?.GetValueOrDefault("Queue") ?? "",
                        value.RawFields?.GetValueOrDefault("Channel") ?? "",
                        value.RawFields?.GetValueOrDefault("CallerIDNum"),
                        int.TryParse(value.RawFields?.GetValueOrDefault("Position"), out var pos) ? pos : 0);
                    break;

                case "QueueCallerLeave":
                    server.Queues.OnCallerLeft(
                        value.RawFields?.GetValueOrDefault("Queue") ?? "",
                        value.RawFields?.GetValueOrDefault("Channel") ?? "");
                    break;

                // Agent events
                case "AgentLogin":
                    server.Agents.OnAgentLogin(
                        value.RawFields?.GetValueOrDefault("Agent") ?? "",
                        value.RawFields?.GetValueOrDefault("Channel"));
                    break;

                case "AgentLogoff":
                    server.Agents.OnAgentLogoff(
                        value.RawFields?.GetValueOrDefault("Agent") ?? "");
                    break;

                case "AgentConnect":
                    server.Agents.OnAgentConnect(
                        value.RawFields?.GetValueOrDefault("Agent") ?? "",
                        value.RawFields?.GetValueOrDefault("Channel"));
                    break;

                case "AgentComplete":
                    server.Agents.OnAgentComplete(
                        value.RawFields?.GetValueOrDefault("Agent") ?? "");
                    break;

                // MeetMe events
                case "MeetMeJoin":
                case "ConfbridgeJoin":
                    server.MeetMe.OnUserJoined(
                        value.RawFields?.GetValueOrDefault("Meetme") ?? value.RawFields?.GetValueOrDefault("Conference") ?? "",
                        int.TryParse(value.RawFields?.GetValueOrDefault("Usernum"), out var un) ? un : 0,
                        value.RawFields?.GetValueOrDefault("Channel") ?? "");
                    break;

                case "MeetMeLeave":
                case "ConfbridgeLeave":
                    server.MeetMe.OnUserLeft(
                        value.RawFields?.GetValueOrDefault("Meetme") ?? value.RawFields?.GetValueOrDefault("Conference") ?? "",
                        int.TryParse(value.RawFields?.GetValueOrDefault("Usernum"), out var unl) ? unl : 0);
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
