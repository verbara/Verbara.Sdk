using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// The path a queue call's AMI events take, for tests that write those events by hand: a
/// <see cref="VerbaraServer"/> over a substitute connection, the event observer that server
/// subscribed in <see cref="VerbaraServer.StartAsync"/>, a <see cref="CallSessionManager"/> on the
/// default <see cref="InMemorySessionStore"/> or a store the test gives it, and a <see cref="QueueSessionTracker"/> on that manager.
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
    private readonly ReloadableConnection _connection;

    private QueueCallRig(VerbaraServer server, CallSessionManager manager, QueueSessionTracker tracker, ReloadableConnection connection)
    {
        Server = server;
        Manager = manager;
        Tracker = tracker;
        _connection = connection;
        _subscription = manager.Events.Subscribe(_events.Add);
    }

    public VerbaraServer Server { get; }

    public CallSessionManager Manager { get; }

    public QueueSessionTracker Tracker { get; }

    /// <summary>Every domain event the manager published, in order.</summary>
    public IReadOnlyList<SessionDomainEvent> Events => _events;

    /// <summary>The <see cref="CallConnectedEvent"/>s the manager published, in order.</summary>
    public IReadOnlyList<CallConnectedEvent> Connected => [.. _events.OfType<CallConnectedEvent>()];

    /// <summary>The <see cref="CallQueuedEvent"/>s the manager published, in order.</summary>
    public IReadOnlyList<CallQueuedEvent> Queued => [.. _events.OfType<CallQueuedEvent>()];

    /// <summary>
    /// Starts a server whose state-load actions return nothing outside <see cref="ReconnectAsync"/>, and
    /// attaches a manager and a tracker to it.
    /// </summary>
    /// <param name="clock">
    /// Optional. The manager's clock seam, the internal constructor's <see cref="TimeProvider"/>; the
    /// system clock when omitted, as in production. A test that moves it between deliveries fixes how
    /// much time the manager sees pass between two frames.
    /// </param>
    /// <param name="store">
    /// Optional. The manager's session store; a fresh <see cref="InMemorySessionStore"/> when omitted. A test that
    /// reads what the manager saved passes a <see cref="RecordingSessionStore"/>.
    /// </param>
    public static async Task<QueueCallRig> StartAsync(TimeProvider? clock = null, SessionStoreBase? store = null)
    {
        var connection = new ReloadableConnection();
        var options = Options.Create(new SessionOptions());
        var server = new VerbaraServer(connection.Connection, connection.ServerLogger);
        var manager = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, store ?? new InMemorySessionStore(),
            clock ?? TimeProvider.System);
        var tracker = new QueueSessionTracker(manager, options);
        try
        {
            manager.AttachToServer(server, ServerId);
            await server.StartAsync();

            // Throws now, not at the first delivery, if the server subscribed no event observer.
            _ = connection.Observer;
            return new QueueCallRig(server, manager, tracker, connection);
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
            _connection.Observer.OnNext(frame);
    }

    /// <summary>
    /// Makes the connection reconnect and runs the server's reload, answered with <paramref name="status"/>
    /// for <c>Status</c> and <paramref name="queueStatus"/> for <c>QueueStatus</c>, and returns once the
    /// reload has read both. Frames delivered afterwards go to the observer the reload subscribed.
    /// </summary>
    /// <remarks>
    /// The reload reconciles the channel table against <paramref name="status"/>, as it does against
    /// Asterisk's own answer: a channel the table holds and the snapshot omits is removed. So the snapshot
    /// lists every channel still up (<see cref="QueueFrames.Status"/>). The manager reads its clock when it
    /// handles each queue entry, so a test sets the clock to the reload instant first.
    /// </remarks>
    public Task ReconnectAsync(IReadOnlyList<ManagerEvent> status, IReadOnlyList<ManagerEvent> queueStatus) =>
        _connection.ReloadAsync(status, queueStatus);

    /// <summary>
    /// Takes frames off <paramref name="frames"/> and delivers them, up to and including the first one
    /// <paramref name="last"/> matches. Throws when none does, since the test's premise is then wrong.
    /// </summary>
    public void DeliverThrough(Queue<ManagerEvent> frames, Func<ManagerEvent, bool> last)
    {
        while (frames.TryDequeue(out var frame))
        {
            _connection.Observer.OnNext(frame);
            if (last(frame))
                return;
        }

        throw new InvalidOperationException("No frame left matches the one to deliver through.");
    }

    /// <summary>
    /// Like <see cref="ReconnectAsync"/>, and <paramref name="script"/> also delivers live frames while the reload runs,
    /// or cuts its queue snapshot off before it completes (<see cref="ReloadScript"/>).
    /// </summary>
    public Task ReconnectAsync(IReadOnlyList<ManagerEvent> status, IReadOnlyList<ManagerEvent> queueStatus, ReloadScript script) =>
        _connection.ReloadAsync(status, queueStatus, script);

    /// <summary>Every count the tracker keeps for <paramref name="queue"/>, or zeros for a queue it never saw.</summary>
    public QueueTally Tally(string queue) =>
        Tracker.GetByQueueName(queue) is { } q
            ? new QueueTally(q.CallsOffered, q.CallsAnswered, q.CallsAbandoned, q.CallsTimedOut, q.CallsWaiting)
            : new QueueTally(0, 0, 0, 0, 0);

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
}

/// <summary>A queue's answered, abandoned and waiting counts, as <see cref="QueueCallRig.Counts"/> reads them.</summary>
internal readonly record struct QueueOutcome(int Answered, int Abandoned, int Waiting);

/// <summary>Every count a queue keeps, as <see cref="QueueCallRig.Tally"/> reads them.</summary>
internal readonly record struct QueueTally(int Offered, int Answered, int Abandoned, int TimedOut, int Waiting);

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

    /// <summary>
    /// app_queue's report that it counted the caller's visit in <paramref name="queue"/> abandoned. Asterisk sends it,
    /// in the <c>agent</c> class, just before the caller's <see cref="Leave"/> for every visit it counts abandoned: the
    /// caller hanging up, the queue's timeout, the queue emptying, a withdrawal and a redirect. It sends none for a
    /// caller that leaves by key, nor for a visit it connects.
    /// </summary>
    public static QueueCallerAbandonEvent Abandon(string queue, string channel, string uniqueId, int holdTime, string? linkedId = null) => new()
    {
        EventType = "QueueCallerAbandon", UniqueId = uniqueId, LinkedId = linkedId ?? uniqueId, HoldTime = holdTime,
        Position = 1, OriginalPosition = 1,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = queue, ["Channel"] = channel, ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId ?? uniqueId,
            ["HoldTime"] = holdTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Position"] = "1", ["OriginalPosition"] = "1",
        },
    };

    /// <summary>
    /// A channel variable set on the caller's channel, in the <c>dialplan</c> class. app_queue sets
    /// <c>ABANDONED</c> before its abandon report and, as the queue application returns, <c>QUEUESTATUS</c>
    /// (<c>TIMEOUT</c>, <c>LEAVEEMPTY</c>, <c>WITHDRAW</c>, …) after the caller's leave.
    /// </summary>
    public static VarSetEvent VarSet(string uniqueId, string channel, string variable, string value, string? linkedId = null) => new()
    {
        EventType = "VarSet", UniqueId = uniqueId, Channel = channel, Variable = variable, Value = value,
        LinkedId = linkedId ?? uniqueId,
        RawFields = new Dictionary<string, string>
        {
            ["Channel"] = channel, ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId ?? uniqueId,
            ["Variable"] = variable, ["Value"] = value,
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

    // --- A reload's snapshot ------------------------------------------------------------------------
    //
    // Shaped as Asterisk 22.9.0 answered Status and QueueStatus, sent on a raw manager connection, for a
    // caller waiting in a queue while a member's leg rang: the fields VerbaraServer's reload reads, with
    // Asterisk's names, and the numeric ChannelState it reads the state from.

    /// <summary>
    /// One channel of a <c>Status</c> answer. <paramref name="state"/> is Asterisk's numeric
    /// <c>ChannelState</c> (4 a caller waiting in a queue, 5 a member's leg ringing, 6 up).
    /// </summary>
    public static StatusEvent Status(string uniqueId, string channel, string linkedId, string state,
        string? callerIdNum = null, string? context = null, string? exten = null, string? application = null,
        string? data = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Channel"] = channel, ["ChannelState"] = state, ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId,
            ["Type"] = "PJSIP", ["Seconds"] = "1",
        };
        if (callerIdNum is not null)
            fields["CallerIDNum"] = callerIdNum;
        if (context is not null)
            fields["Context"] = context;
        if (exten is not null)
            fields["Exten"] = exten;
        if (application is not null)
            fields["Application"] = application;
        if (data is not null)
            fields["Data"] = data;

        return new()
        {
            EventType = "Status", UniqueId = uniqueId, Channel = channel, LinkedId = linkedId, Extension = exten,
            Application = application, Data = data, RawFields = fields,
        };
    }

    /// <summary>A queue's parameters, the first frame of a <c>QueueStatus</c> answer for it.</summary>
    public static QueueParamsEvent QueueParams(string queue, int calls) => new()
    {
        EventType = "QueueParams", Queue = queue, Max = 0, Strategy = "ringall", Calls = calls, HoldTime = 0,
        TalkTime = 0, Completed = 0, Abandoned = 0,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = queue, ["Max"] = "0", ["Strategy"] = "ringall",
            ["Calls"] = calls.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
    };

    /// <summary>
    /// A caller waiting in <paramref name="queue"/>, as a <c>QueueStatus</c> answer lists it.
    /// <paramref name="wait"/> is the <c>Wait</c> header, the whole seconds the caller has waited in this
    /// queue; <c>null</c> leaves the header out, a frame no measured version sends.
    /// </summary>
    public static QueueEntryEvent Entry(string queue, string channel, string uniqueId, string callerIdNum, long? wait,
        int position = 1)
    {
        var fields = new Dictionary<string, string>
        {
            ["Queue"] = queue, ["Position"] = position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Channel"] = channel, ["Uniqueid"] = uniqueId, ["CallerIDNum"] = callerIdNum,
            ["ConnectedLineNum"] = "unknown", ["ConnectedLineName"] = "unknown", ["Priority"] = "0",
        };
        if (wait is { } w)
            fields["Wait"] = w.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new()
        {
            EventType = "QueueEntry", Queue = queue, Position = position, Channel = channel, Uniqueid = uniqueId,
            CallerIDNum = callerIdNum, Wait = wait, Priority = 0, RawFields = fields,
        };
    }
}
