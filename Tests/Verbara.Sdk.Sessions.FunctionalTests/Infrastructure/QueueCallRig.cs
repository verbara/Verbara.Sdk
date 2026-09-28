using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// The path a queue call's AMI events take, for tests that write those events by hand: a
/// <see cref="VerbaraServer"/> over a substitute connection, the event observer that server
/// subscribed in <see cref="VerbaraServer.StartAsync"/>, a <see cref="CallSessionManager"/> on the
/// default <see cref="InMemorySessionStore"/>, and a <see cref="QueueSessionTracker"/> on that manager.
/// </summary>
/// <remarks>
/// Every frame goes to the server's own observer, not to the managers' entry points, so a test sees
/// what the production switch does with it. Unlike a capture replay, an exception the observer throws
/// is not caught: a typed test fails on it. Delivery is synchronous and every session transition runs
/// on the delivering thread, so nothing waits. A test that needs time to pass between two frames gives
/// the manager its own clock (<see cref="StartAsync"/>) and moves it between deliveries.
/// </remarks>
internal sealed class QueueCallRig : IAsyncDisposable
{
    public const string ServerId = "queue-srv";

    private readonly List<SessionDomainEvent> _events = [];
    private readonly IDisposable _subscription;
    private readonly IObserver<ManagerEvent> _observer;

    private QueueCallRig(VerbaraServer server, CallSessionManager manager, QueueSessionTracker tracker, IObserver<ManagerEvent> observer)
    {
        Server = server;
        Manager = manager;
        Tracker = tracker;
        _observer = observer;
        _subscription = manager.Events.Subscribe(_events.Add);
    }

    public VerbaraServer Server { get; }

    public CallSessionManager Manager { get; }

    public QueueSessionTracker Tracker { get; }

    /// <summary>Every domain event the manager published, in order.</summary>
    public IReadOnlyList<SessionDomainEvent> Events => _events;

    /// <summary>The <see cref="CallConnectedEvent"/>s the manager published, in order.</summary>
    public IReadOnlyList<CallConnectedEvent> Connected => [.. _events.OfType<CallConnectedEvent>()];

    /// <summary>Starts a server whose state-load actions return nothing, and attaches a manager and a tracker to it.</summary>
    /// <param name="clock">
    /// Optional. The manager's clock seam, the internal constructor's <see cref="TimeProvider"/>; the
    /// system clock when omitted, as in production. A test that moves it between deliveries fixes how
    /// much time the manager sees pass between two frames.
    /// </param>
    public static async Task<QueueCallRig> StartAsync(TimeProvider? clock = null)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => NoEvents());
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => observer = o))
            .Returns(Substitute.For<IDisposable>());

        var options = Options.Create(new SessionOptions());
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, new InMemorySessionStore(),
            clock ?? TimeProvider.System);
        var tracker = new QueueSessionTracker(manager, options);
        try
        {
            manager.AttachToServer(server, ServerId);
            await server.StartAsync();
            return new QueueCallRig(server, manager, tracker, observer
                ?? throw new InvalidOperationException("VerbaraServer.StartAsync subscribed no event observer."));
        }
        catch
        {
            tracker.Dispose();
            await manager.DisposeAsync();
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>Delivers <paramref name="frames"/> to the server's observer, in order.</summary>
    public void Deliver(IEnumerable<ManagerEvent> frames)
    {
        foreach (var frame in frames)
            _observer.OnNext(frame);
    }

    /// <summary>
    /// Takes frames off <paramref name="frames"/> and delivers them, up to and including the first one
    /// <paramref name="last"/> matches. Throws when none does, since the test's premise is then wrong.
    /// </summary>
    public void DeliverThrough(Queue<ManagerEvent> frames, Func<ManagerEvent, bool> last)
    {
        while (frames.TryDequeue(out var frame))
        {
            _observer.OnNext(frame);
            if (last(frame))
                return;
        }

        throw new InvalidOperationException("No frame left matches the one to deliver through.");
    }

    /// <summary>The queue's answered, abandoned and waiting counts, or zeros for a queue the tracker never saw.</summary>
    public QueueOutcome Counts(string queue) =>
        Tracker.GetByQueueName(queue) is { } q
            ? new QueueOutcome(q.CallsAnswered, q.CallsAbandoned, q.CallsWaiting)
            : new QueueOutcome(0, 0, 0);

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        Tracker.Dispose();
        await Manager.DisposeAsync();
        await Server.DisposeAsync();
    }

    private static async IAsyncEnumerable<ManagerEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>A queue's answered, abandoned and waiting counts, as <see cref="QueueCallRig.Counts"/> reads them.</summary>
internal readonly record struct QueueOutcome(int Answered, int Abandoned, int Waiting);

/// <summary>
/// Typed AMI frames of a queue call, carrying the fields <see cref="VerbaraServer"/>'s observer reads,
/// with the names Asterisk gives them. A queue frame's queue, channel and caller number travel in
/// <see cref="ManagerEvent.RawFields"/>, where the observer reads them, as a parsed frame carries them.
/// </summary>
internal static class QueueFrames
{
    public static NewChannelEvent NewChannel(string uniqueId, string channel, string state, string linkedId,
        string? callerIdNum = null, string? context = null, string? exten = null) => new()
        {
            EventType = "Newchannel", UniqueId = uniqueId, Channel = channel, ChannelState = state,
            CallerIdNum = callerIdNum, Context = context, Exten = exten, Priority = 1, Linkedid = linkedId,
        };

    public static NewStateEvent NewState(string uniqueId, string state) =>
        new() { EventType = "Newstate", UniqueId = uniqueId, ChannelState = state };

    /// <summary>
    /// The caller joining <paramref name="queue"/>. Its <c>Linkedid</c> is the caller's own
    /// <paramref name="uniqueId"/> unless <paramref name="linkedId"/> names the call's first channel, as it
    /// does for a caller that is not that channel.
    /// </summary>
    public static QueueCallerJoinEvent Join(string queue, string channel, string uniqueId, string callerIdNum, int position = 1,
        string? linkedId = null) => new()
        {
            EventType = "QueueCallerJoin", UniqueId = uniqueId, LinkedId = linkedId ?? uniqueId, Position = position,
            RawFields = new Dictionary<string, string>
            {
                ["Queue"] = queue, ["Channel"] = channel, ["CallerIDNum"] = callerIdNum,
                ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId ?? uniqueId,
                ["Position"] = position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };

    /// <summary>The caller leaving <paramref name="queue"/>; <paramref name="linkedId"/> as for <see cref="Join"/>.</summary>
    public static QueueCallerLeaveEvent Leave(string queue, string channel, string uniqueId, string? linkedId = null) => new()
    {
        EventType = "QueueCallerLeave", UniqueId = uniqueId, LinkedId = linkedId ?? uniqueId, Position = 1,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = queue, ["Channel"] = channel, ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId ?? uniqueId,
        },
    };

    public static DialBeginEvent DialBegin(string uniqueId, string channel, string destUniqueId, string destChannel, string? dialString = null) => new()
    {
        EventType = "DialBegin", UniqueId = uniqueId, Channel = channel, DestUniqueid = destUniqueId,
        DestChannel = destChannel, DialString = dialString,
    };

    public static DialEndEvent DialEnd(string uniqueId, string channel, string destUniqueId, string destChannel, string status) => new()
    {
        EventType = "DialEnd", UniqueId = uniqueId, Channel = channel, DestUniqueid = destUniqueId,
        DestChannel = destChannel, DialStatus = status,
    };

    /// <summary>
    /// app_queue's report that it connected the caller to a member. As on every captured frame, it
    /// carries no <c>Agent</c> header unless <paramref name="agent"/> is given: the member is named by
    /// <c>MemberName</c> and <c>Interface</c>. With <paramref name="agent"/>, it names an agent the SDK
    /// may know by name from an <see cref="AgentLogin"/> — a shape no capture holds.
    /// </summary>
    public static AgentConnectEvent AgentConnect(string queue, string callerUniqueId, string callerChannel, string linkedId,
        string member, string memberUniqueId, string memberChannel, long holdTime, string? agent = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Channel"] = callerChannel, ["Uniqueid"] = callerUniqueId, ["Linkedid"] = linkedId,
            ["DestChannel"] = memberChannel, ["DestUniqueid"] = memberUniqueId, ["Queue"] = queue,
            ["Interface"] = member, ["MemberName"] = member,
            ["HoldTime"] = holdTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (agent is not null)
            fields["Agent"] = agent;

        return new()
        {
            EventType = "AgentConnect", UniqueId = callerUniqueId, Channel = callerChannel, LinkedId = linkedId,
            Interface = member, HoldTime = holdTime, DestUniqueId = memberUniqueId, DestChannel = memberChannel,
            Agent = agent, RawFields = fields,
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

    public static AgentCompleteEvent AgentComplete(string callerUniqueId, string callerChannel, string member, long holdTime, long talkTime) => new()
    {
        EventType = "AgentComplete", UniqueId = callerUniqueId, Channel = callerChannel, Interface = member,
        HoldTime = holdTime, TalkTime = talkTime,
    };

    public static BridgeCreateEvent BridgeCreate(string bridgeId) =>
        new() { EventType = "BridgeCreate", BridgeUniqueid = bridgeId, BridgeType = "basic", BridgeTechnology = "simple_bridge" };

    public static BridgeEnterEvent BridgeEnter(string bridgeId, string uniqueId) =>
        new() { EventType = "BridgeEnter", BridgeUniqueid = bridgeId, UniqueId = uniqueId };

    public static BridgeLeaveEvent BridgeLeave(string bridgeId, string uniqueId) =>
        new() { EventType = "BridgeLeave", BridgeUniqueid = bridgeId, UniqueId = uniqueId };

    public static BridgeDestroyEvent BridgeDestroy(string bridgeId) =>
        new() { EventType = "BridgeDestroy", BridgeUniqueid = bridgeId };

    public static UnholdEvent Unhold(string uniqueId) => new() { EventType = "Unhold", UniqueId = uniqueId };

    /// <summary>A member's blind transfer of the caller (the transferee) out of their bridge, to <paramref name="extension"/>.</summary>
    public static BlindTransferEvent BlindTransfer(string bridgeId, string transfereeUniqueId, string transfereeChannel,
        string context, string extension) => new()
        {
            EventType = "BlindTransfer", BridgeUniqueid = bridgeId, TransfereeUniqueId = transfereeUniqueId,
            TransfereeChannel = transfereeChannel, TransfereeContext = context, Extension = extension, Result = "Success",
        };

    public static HangupEvent Hangup(string uniqueId, int cause) => new() { EventType = "Hangup", UniqueId = uniqueId, Cause = cause };
}
