using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The real <see cref="CallSessionManager"/>, on a clock the test gives it, attached to one or more
/// <see cref="VerbaraServer"/>s whose AMI events the test writes by hand. No agent tracker is resolved: what the
/// manager raises on <see cref="CallSessionManager.Events"/> is recorded, and nothing else listens.
/// </summary>
/// <remarks>
/// <para>
/// Each server runs over a substitute <see cref="IAmiConnection"/> whose state-load actions return nothing, and every
/// frame goes to the event observer the server subscribed in <see cref="VerbaraServer.StartAsync"/>: the production
/// switch, not the managers' entry points, so a test sees what that switch does with an <c>AgentConnect</c> or an
/// <c>AgentComplete</c> whatever internal route it takes to the session manager. An exception the observer throws
/// is not caught.
/// </para>
/// <para>
/// Delivery is synchronous, and every session transition runs on the delivering thread. A test moves the clock
/// between deliveries; on <see cref="Harness.FakeTimeProvider"/> a due timer fires inside that move, on the
/// test's thread.
/// </para>
/// </remarks>
internal sealed class WrapUpRig : IAsyncDisposable
{
    private readonly Dictionary<string, (VerbaraServer Server, IObserver<ManagerEvent> Observer)> _servers;
    private readonly List<SessionDomainEvent> _events = [];
    private readonly Lock _eventsGate = new();
    private readonly IDisposable _subscription;
    private bool _managerDisposed;

    private WrapUpRig(CallSessionManager manager,
        Dictionary<string, (VerbaraServer Server, IObserver<ManagerEvent> Observer)> servers)
    {
        Manager = manager;
        _servers = servers;
        _subscription = manager.Events.Subscribe(new Recorder(this));
    }

    public CallSessionManager Manager { get; }

    /// <summary>Whether the manager's event stream has completed.</summary>
    public bool Completed { get; private set; }

    /// <summary>Every domain event the manager raised, in order.</summary>
    public IReadOnlyList<SessionDomainEvent> Events
    {
        get
        {
            lock (_eventsGate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The <see cref="CallWrapUpEvent"/>s the manager raised, in order.</summary>
    public IReadOnlyList<CallWrapUpEvent> WrapUps => [.. Events.OfType<CallWrapUpEvent>()];

    /// <summary>
    /// Starts one server per id in <paramref name="serverIds"/> and attaches the manager, built on
    /// <paramref name="clock"/> through its internal constructor, to each under that id.
    /// </summary>
    public static async Task<WrapUpRig> StartAsync(TimeProvider clock, SessionOptions? options = null,
        params string[] serverIds)
    {
        if (serverIds.Length == 0)
            serverIds = [WrapUpFrames.ServerId];

        var manager = new CallSessionManager(Options.Create(options ?? new SessionOptions()),
            NullLogger<CallSessionManager>.Instance, new InMemorySessionStore(), clock);
        var servers = new Dictionary<string, (VerbaraServer, IObserver<ManagerEvent>)>(StringComparer.Ordinal);
        try
        {
            foreach (var serverId in serverIds)
            {
                var connection = Substitute.For<IAmiConnection>();
                connection.AsteriskVersion.Returns("22.10.1");
                connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
                    .Returns(_ => NoEvents());
                IObserver<ManagerEvent>? observer = null;
                connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => observer = o))
                    .Returns(Substitute.For<IDisposable>());

                var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
                manager.AttachToServer(server, serverId);
                await server.StartAsync();
                servers[serverId] = (server, observer
                    ?? throw new InvalidOperationException("VerbaraServer.StartAsync subscribed no event observer."));
            }

            return new WrapUpRig(manager, servers);
        }
        catch
        {
            await manager.DisposeAsync();
            foreach (var (server, _) in servers.Values)
                await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>Delivers <paramref name="frames"/>, in order, to the observer of the default server.</summary>
    public void Deliver(params ManagerEvent[] frames) => DeliverTo(WrapUpFrames.ServerId, frames);

    /// <summary>Delivers <paramref name="frames"/>, in order, to the observer of server <paramref name="serverId"/>.</summary>
    public void DeliverTo(string serverId, params ManagerEvent[] frames)
    {
        var observer = _servers[serverId].Observer;
        foreach (var frame in frames)
            observer.OnNext(frame);
    }

    /// <summary>The session the manager holds for <paramref name="linkedId"/>; throws when it holds none.</summary>
    public CallSession Session(string linkedId) =>
        Manager.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"premise: the manager holds a call under linkedid {linkedId}");

    /// <summary>Disposes the manager once; <see cref="DisposeAsync"/> then leaves it alone.</summary>
    public async ValueTask DisposeManagerAsync()
    {
        _managerDisposed = true;
        await Manager.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        if (!_managerDisposed)
            await Manager.DisposeAsync();
        foreach (var (server, _) in _servers.Values)
            await server.DisposeAsync();
    }

    private static async IAsyncEnumerable<ManagerEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class Recorder(WrapUpRig rig) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            lock (rig._eventsGate)
            {
                rig._events.Add(value);
            }
        }

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to record.
        }

        public void OnCompleted() => rig.Completed = true;
    }
}

/// <summary>
/// Typed AMI frames of the calls an agent's wrap-up follows, carrying the fields <see cref="VerbaraServer"/>'s
/// observer reads, with the names and the order Asterisk gives them. Shaped on the frames captured from Asterisk
/// 20.20.1, 22.10.1 and 23.4.1 with app_queue's <c>wrapuptime</c> set, and, for a blind transfer, on a first capture
/// taken on 22.10.1: <c>BlindTransfer</c>, then the member's <c>BridgeLeave</c>, then <c>AgentComplete</c>
/// with <c>Reason: transfer</c>, while the caller stays up in the transfer's context. On every captured
/// <c>AgentConnect</c> and <c>AgentComplete</c> the caller is <c>Uniqueid</c>, the member's leg
/// <c>DestUniqueid</c>, and the member is named by <c>Interface</c> and <c>MemberName</c>, with no <c>Agent</c>
/// header.
/// </summary>
internal static class WrapUpFrames
{
    public const string ServerId = "srv-1";

    public static NewChannelEvent NewChannel(string uniqueId, string channel, string state, string linkedId,
        string? context = null, string? exten = null) => new()
        {
            EventType = "Newchannel", UniqueId = uniqueId, Channel = channel, ChannelState = state,
            CallerIdNum = "5551234", Context = context, Exten = exten, Priority = 1, Linkedid = linkedId,
            RawFields = new Dictionary<string, string>
            {
                ["Channel"] = channel, ["ChannelStateDesc"] = state, ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId,
                ["CallerIDNum"] = "5551234",
            },
        };

    public static NewStateEvent NewState(string uniqueId, string state) =>
        new() { EventType = "Newstate", UniqueId = uniqueId, ChannelState = state };

    /// <summary>The caller joining <paramref name="queue"/>.</summary>
    public static QueueCallerJoinEvent Join(string queue, string channel, string uniqueId, string linkedId) => new()
    {
        EventType = "QueueCallerJoin", UniqueId = uniqueId, LinkedId = linkedId, Position = 1,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = queue, ["Channel"] = channel, ["CallerIDNum"] = "5551234",
            ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId, ["Position"] = "1",
        },
    };

    /// <summary>The caller leaving <paramref name="queue"/>: app_queue sends it just before its connect report.</summary>
    public static QueueCallerLeaveEvent Leave(string queue, string channel, string uniqueId, string linkedId) => new()
    {
        EventType = "QueueCallerLeave", UniqueId = uniqueId, LinkedId = linkedId, Position = 1,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = queue, ["Channel"] = channel, ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId,
        },
    };

    /// <summary>
    /// app_queue's report that it connected the caller to <paramref name="member"/>. As on every captured frame, it
    /// carries no <c>Agent</c> header unless <paramref name="agent"/> is given; with it, it names an agent the SDK
    /// knows by name from an <see cref="AgentLogin"/>, a shape no capture holds.
    /// </summary>
    public static AgentConnectEvent AgentConnect(string queue, string callerUniqueId, string callerChannel,
        string linkedId, string member, string memberUniqueId, string memberChannel, string? agent = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Channel"] = callerChannel, ["Uniqueid"] = callerUniqueId, ["Linkedid"] = linkedId,
            ["DestChannel"] = memberChannel, ["DestUniqueid"] = memberUniqueId, ["DestLinkedid"] = linkedId,
            ["Queue"] = queue, ["Interface"] = member, ["MemberName"] = member, ["HoldTime"] = "0",
        };
        if (agent is not null)
            fields["Agent"] = agent;

        return new()
        {
            EventType = "AgentConnect", UniqueId = callerUniqueId, Channel = callerChannel, LinkedId = linkedId,
            Interface = member, HoldTime = 0, DestUniqueId = memberUniqueId, DestChannel = memberChannel,
            DestLinkedId = linkedId, Agent = agent, RawFields = fields,
        };
    }

    /// <summary>
    /// app_queue's report that <paramref name="member"/> left the caller: <c>Reason: agent</c> when the member hung
    /// up, <c>caller</c> when the caller did, <c>transfer</c> when the member transferred the caller away.
    /// </summary>
    public static AgentCompleteEvent AgentComplete(string queue, string callerUniqueId, string callerChannel,
        string linkedId, string member, string memberUniqueId, string memberChannel, long talkTime,
        string reason = "agent", string? agent = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Channel"] = callerChannel, ["Uniqueid"] = callerUniqueId, ["Linkedid"] = linkedId,
            ["DestChannel"] = memberChannel, ["DestUniqueid"] = memberUniqueId, ["DestLinkedid"] = linkedId,
            ["Queue"] = queue, ["Interface"] = member, ["MemberName"] = member, ["HoldTime"] = "0",
            ["TalkTime"] = talkTime.ToString(CultureInfo.InvariantCulture), ["Reason"] = reason,
        };
        if (agent is not null)
            fields["Agent"] = agent;

        return new()
        {
            EventType = "AgentComplete", UniqueId = callerUniqueId, Channel = callerChannel, LinkedId = linkedId,
            Interface = member, HoldTime = 0, TalkTime = talkTime, Reason = reason, DestUniqueId = memberUniqueId,
            DestChannel = memberChannel, DestLinkedId = linkedId, Agent = agent, RawFields = fields,
        };
    }

    /// <summary>An agent logging in, as app_agent_pool reports it: the SDK then knows the agent by name.</summary>
    public static AgentLoginEvent AgentLogin(string agent, string channel, string uniqueId) => new()
    {
        EventType = "AgentLogin", UniqueId = uniqueId, Agent = agent, Channel = channel,
        RawFields = new Dictionary<string, string>
        {
            ["Channel"] = channel, ["Uniqueid"] = uniqueId, ["Linkedid"] = uniqueId, ["Agent"] = agent,
        },
    };

    public static BridgeCreateEvent BridgeCreate(string bridgeId) =>
        new() { EventType = "BridgeCreate", BridgeUniqueid = bridgeId, BridgeType = "basic", BridgeTechnology = "simple_bridge" };

    public static BridgeEnterEvent BridgeEnter(string bridgeId, string uniqueId) =>
        new() { EventType = "BridgeEnter", BridgeUniqueid = bridgeId, UniqueId = uniqueId };

    public static BridgeLeaveEvent BridgeLeave(string bridgeId, string uniqueId) =>
        new() { EventType = "BridgeLeave", BridgeUniqueid = bridgeId, UniqueId = uniqueId };

    /// <summary>The member's blind transfer of the caller (the transferee) out of their bridge, to <paramref name="extension"/>.</summary>
    public static BlindTransferEvent BlindTransfer(string bridgeId, string transfereeUniqueId, string transfereeChannel,
        string context, string extension) => new()
        {
            EventType = "BlindTransfer", BridgeUniqueid = bridgeId, TransfereeUniqueId = transfereeUniqueId,
            TransfereeChannel = transfereeChannel, TransfereeContext = context, Extension = extension, Result = "Success",
        };

    /// <summary>A hangup with cause 16, normal clearing.</summary>
    public static HangupEvent Hangup(string uniqueId) => new() { EventType = "Hangup", UniqueId = uniqueId, Cause = 16 };

    /// <summary>
    /// One queue call's legs as the manager sees them up to app_queue's connect: the caller's channel, its join of
    /// <paramref name="queue"/>, the member's leg, both answered, the leave and the connect. The caller and its call
    /// are <paramref name="caller"/>; the member's leg is <paramref name="memberLeg"/>.
    /// </summary>
    public static ManagerEvent[] QueueCallConnected(string queue, string caller, string memberLeg, string member,
        string? agent = null, bool withConnect = true)
    {
        var callerChannel = CallerChannel(caller);
        var memberChannel = MemberChannel(member, memberLeg);
        List<ManagerEvent> frames =
        [
            NewChannel(caller, callerChannel, "Ring", caller, context: "from-trunk", exten: "500"),
            Join(queue, callerChannel, caller, caller),
            NewChannel(memberLeg, memberChannel, "Down", caller, context: "agents"),
            NewState(memberLeg, "Up"),
            Leave(queue, callerChannel, caller, caller),
        ];
        if (withConnect)
            frames.Add(AgentConnect(queue, caller, callerChannel, caller, member, memberLeg, memberChannel, agent));

        frames.Add(NewState(caller, "Up"));
        frames.Add(BridgeCreate(Bridge(caller)));
        frames.Add(BridgeEnter(Bridge(caller), memberLeg));
        frames.Add(BridgeEnter(Bridge(caller), caller));
        return [.. frames];
    }

    /// <summary>
    /// The member hangs up a connected queue call, as the captures order it: the member's leg leaves and hangs up,
    /// app_queue reports <c>AgentComplete</c> (<c>Reason: agent</c>), then the caller's leg hangs up.
    /// </summary>
    public static ManagerEvent[] MemberHangsUp(string queue, string caller, string memberLeg, string member,
        long talkTime = 60, string? agent = null) =>
    [
        BridgeLeave(Bridge(caller), memberLeg),
        Hangup(memberLeg),
        AgentComplete(queue, caller, CallerChannel(caller), caller, member, memberLeg, MemberChannel(member, memberLeg),
            talkTime, "agent", agent),
        BridgeLeave(Bridge(caller), caller),
        Hangup(caller),
    ];

    /// <summary>
    /// A call outside any queue that <paramref name="agent"/>, known by name, takes on <paramref name="member"/>: the
    /// caller's channel rings, the agent's leg answers, the connect names the agent, and both legs enter one bridge.
    /// The manager never saw the caller join a queue, so the call has no queue.
    /// </summary>
    public static ManagerEvent[] NamedAgentCallConnected(string caller, string agentLeg, string member, string agent) =>
    [
        NewChannel(caller, CallerChannel(caller), "Down", caller, context: "from-trunk", exten: "600"),
        NewState(caller, "Ring"),
        NewChannel(agentLeg, MemberChannel(member, agentLeg), "Down", caller, context: "agents"),
        NewState(agentLeg, "Up"),
        AgentConnect("", caller, CallerChannel(caller), caller, member, agentLeg, MemberChannel(member, agentLeg), agent),
        NewState(caller, "Up"),
        BridgeCreate(Bridge(caller)),
        BridgeEnter(Bridge(caller), agentLeg),
        BridgeEnter(Bridge(caller), caller),
    ];

    /// <summary>Both legs of a call <see cref="NamedAgentCallConnected"/> connected hang up, the agent's first.</summary>
    public static ManagerEvent[] NamedAgentCallEnds(string caller, string agentLeg) =>
    [
        BridgeLeave(Bridge(caller), agentLeg),
        Hangup(agentLeg),
        BridgeLeave(Bridge(caller), caller),
        Hangup(caller),
    ];

    public static string CallerChannel(string caller) => $"PJSIP/trunk-{caller}";

    public static string MemberChannel(string member, string memberLeg) => $"{member}-{memberLeg}";

    public static string Bridge(string caller) => $"bridge-{caller}";
}
