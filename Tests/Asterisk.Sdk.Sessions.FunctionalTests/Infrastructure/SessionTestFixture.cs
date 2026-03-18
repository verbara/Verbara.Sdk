using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Internal;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// Reusable test fixture that wires up the real Live + Session pipeline
/// without any network I/O. Provides convenience methods to simulate
/// AMI-driven channel lifecycle events (new channel, dial, answer,
/// bridge, hold, hangup, queue join) and exposes the
/// <see cref="CallSessionManager"/> for assertions.
/// </summary>
public sealed class SessionTestFixture : IAsyncLifetime
{
    private const string DefaultServerId = "test-srv";
    private readonly IAmiConnection _connection;

    public AsteriskServer Server { get; }
    public CallSessionManager SessionManager { get; }
    public SessionOptions Options { get; }

    public SessionTestFixture()
        : this(new SessionOptions())
    {
    }

    public SessionTestFixture(SessionOptions options)
    {
        Options = options;
        _connection = Substitute.For<IAmiConnection>();
        _connection.AsteriskVersion.Returns("21.0.0");

        Server = new AsteriskServer(_connection, NullLogger<AsteriskServer>.Instance);
        SessionManager = new CallSessionManager(
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());

        SessionManager.AttachToServer(Server, DefaultServerId);
    }

    // ---- Channel lifecycle helpers ----

    /// <summary>
    /// Simulate a new channel appearing (maps to AMI Newchannel event).
    /// Returns the unique ID used.
    /// </summary>
    public string SimulateNewChannel(
        string uniqueId,
        string channelName,
        ChannelState state = ChannelState.Ring,
        string? linkedId = null,
        string? callerIdNum = null,
        string? callerIdName = null,
        string? context = null,
        string? exten = null)
    {
        Server.Channels.OnNewChannel(
            uniqueId, channelName, state,
            callerIdNum: callerIdNum,
            callerIdName: callerIdName,
            context: context,
            exten: exten,
            linkedId: linkedId ?? uniqueId);

        return uniqueId;
    }

    /// <summary>
    /// Simulate channel state change (maps to AMI Newstate event).
    /// </summary>
    public void SimulateStateChange(string uniqueId, ChannelState newState)
    {
        Server.Channels.OnNewState(uniqueId, newState);
    }

    /// <summary>
    /// Simulate a Dial begin event.
    /// </summary>
    public void SimulateDialBegin(string sourceUniqueId, string destUniqueId, string destChannel, string? dialString = null)
    {
        Server.Channels.OnDialBegin(sourceUniqueId, destUniqueId, destChannel, dialString);
    }

    /// <summary>
    /// Simulate a Dial end event.
    /// </summary>
    public void SimulateDialEnd(string uniqueId, string dialStatus = "ANSWER")
    {
        Server.Channels.OnDialEnd(uniqueId, dialStatus);
    }

    /// <summary>
    /// Simulate channel answered (state -> Up).
    /// </summary>
    public void SimulateAnswer(string uniqueId)
    {
        SimulateStateChange(uniqueId, ChannelState.Up);
    }

    /// <summary>
    /// Simulate channel hangup (maps to AMI Hangup event).
    /// </summary>
    public void SimulateHangup(string uniqueId, HangupCause cause = HangupCause.NormalClearing)
    {
        Server.Channels.OnHangup(uniqueId, cause);
    }

    /// <summary>
    /// Simulate channel placed on hold.
    /// </summary>
    public void SimulateHold(string uniqueId, string? musicClass = null)
    {
        Server.Channels.OnHold(uniqueId, musicClass);
    }

    /// <summary>
    /// Simulate channel taken off hold.
    /// </summary>
    public void SimulateUnhold(string uniqueId)
    {
        Server.Channels.OnUnhold(uniqueId);
    }

    // ---- Bridge lifecycle helpers ----

    /// <summary>
    /// Simulate a bridge being created.
    /// </summary>
    public void SimulateBridgeCreated(string bridgeId, string? type = "mixing", string? technology = "simple_bridge")
    {
        Server.Bridges.OnBridgeCreated(bridgeId, type, technology, creator: null, name: null);
    }

    /// <summary>
    /// Simulate a channel entering a bridge.
    /// </summary>
    public void SimulateBridgeEnter(string bridgeId, string uniqueId)
    {
        Server.Bridges.OnChannelEntered(bridgeId, uniqueId);
    }

    /// <summary>
    /// Simulate a channel leaving a bridge.
    /// </summary>
    public void SimulateBridgeLeave(string bridgeId, string uniqueId)
    {
        Server.Bridges.OnChannelLeft(bridgeId, uniqueId);
    }

    /// <summary>
    /// Simulate a bridge being destroyed.
    /// </summary>
    public void SimulateBridgeDestroyed(string bridgeId)
    {
        Server.Bridges.OnBridgeDestroyed(bridgeId);
    }

    /// <summary>
    /// Simulate a blind transfer event.
    /// </summary>
    public void SimulateBlindTransfer(string bridgeId, string targetChannel, string? extension = null, string? context = null)
    {
        Server.Bridges.OnBlindTransfer(bridgeId, targetChannel, extension, context);
    }

    /// <summary>
    /// Simulate an attended transfer event.
    /// </summary>
    public void SimulateAttendedTransfer(string origBridgeId, string? secondBridgeId = null, string? destType = null, string? result = null)
    {
        Server.Bridges.OnAttendedTransfer(origBridgeId, secondBridgeId, destType, result);
    }

    // ---- Queue helpers ----

    /// <summary>
    /// Simulate a caller joining a queue.
    /// </summary>
    public void SimulateQueueCallerJoined(string queueName, string channel, string? callerId = null, int position = 1)
    {
        Server.Queues.OnCallerJoined(queueName, channel, callerId, position);
    }

    // ---- Compound scenario helpers ----

    /// <summary>
    /// Simulate a complete inbound call flow: new channel (Ring) -> Ringing -> Dial -> Answer -> Bridge.
    /// Returns (callerUniqueId, agentUniqueId, bridgeId).
    /// </summary>
    public (string CallerUid, string AgentUid, string BridgeId) SimulateInboundCallAnswered(
        string callerUid = "caller-001",
        string callerChannel = "PJSIP/trunk-001",
        string agentUid = "agent-001",
        string agentChannel = "PJSIP/100-001",
        string linkedId = "linked-001",
        string bridgeId = "bridge-001",
        string? context = "from-trunk")
    {
        // Caller channel appears
        SimulateNewChannel(callerUid, callerChannel, ChannelState.Ring,
            linkedId: linkedId, context: context, callerIdNum: "5551234");

        // Agent channel appears (same linkedId => same session)
        SimulateNewChannel(agentUid, agentChannel, ChannelState.Ring,
            linkedId: linkedId);

        // Dial begins
        SimulateDialBegin(callerUid, agentUid, agentChannel);

        // Agent answers
        SimulateAnswer(agentUid);

        // Bridge created and both enter
        SimulateBridgeCreated(bridgeId);
        SimulateBridgeEnter(bridgeId, callerUid);
        SimulateBridgeEnter(bridgeId, agentUid);

        return (callerUid, agentUid, bridgeId);
    }

    /// <summary>
    /// Simulate a complete outbound call flow: new channel -> Dial -> Answer -> Bridge.
    /// Returns (originatorUid, destUid, bridgeId).
    /// </summary>
    public (string OriginatorUid, string DestUid, string BridgeId) SimulateOutboundCallAnswered(
        string originatorUid = "orig-001",
        string originatorChannel = "PJSIP/100-001",
        string destUid = "dest-001",
        string destChannel = "PJSIP/trunk-001",
        string linkedId = "linked-002",
        string bridgeId = "bridge-002",
        string? context = "from-internal")
    {
        SimulateNewChannel(originatorUid, originatorChannel, ChannelState.Ring,
            linkedId: linkedId, context: context, callerIdNum: "100");

        SimulateNewChannel(destUid, destChannel, ChannelState.Ring,
            linkedId: linkedId);

        SimulateDialBegin(originatorUid, destUid, destChannel);
        SimulateAnswer(destUid);

        SimulateBridgeCreated(bridgeId);
        SimulateBridgeEnter(bridgeId, originatorUid);
        SimulateBridgeEnter(bridgeId, destUid);

        return (originatorUid, destUid, bridgeId);
    }

    // ---- IAsyncLifetime ----

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await SessionManager.DisposeAsync();
        await Server.DisposeAsync();
    }
}
