using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The state a call opens in when a <c>Status</c> snapshot reports it does not depend on the order the snapshot lists
/// its legs in: a call whose caller is up is connected whether the snapshot names the caller first or a member leg
/// the queue is ringing (or has just created) first.
/// </summary>
/// <remarks>
/// The frames are written here, shaped on the <c>Status</c>, <c>QueueStatus</c>, <c>QueueCallerAbandon</c>,
/// <c>QueueCallerLeave</c> and <c>Hangup</c> frames of a queued call whose caller was answered by the dialplan before
/// it was queued, and parsed by the SDK's own reader. Asterisk lists the channels of a <c>Status</c> reply in its own
/// container order, which does not follow the order the legs were created in.
/// </remarks>
public sealed class ReloadedCallOpeningStateTests
{
    private const string ServerId = "reload-replay";
    private const string Caller = "1795769507.49";
    private const string CallerChannel = "PJSIP/loop-00000031";
    private const string Member = "1795769510.52";
    private const string MemberChannel = "PJSIP/agent2-00000034";

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task End_ShouldBeCompleted_WhenTheReloadListsTheMemberLegBeforeTheConnectedQueuedCaller(int memberState)
    {
        var (state, end, cause) = await ReloadQueuedThenHangUpAsync(StatusEntries(MemberStatus(memberState), CallerStatus));

        using var scope = new AssertionScope();
        state.Should().Be(CallSessionState.Queued, "premise: the reload opens the call queued");
        end.Should().Be(CallSessionState.Completed,
            "the caller, answered before the reload, hung up with Cause 16 while waiting in the queue");
        cause.Should().Be(HangupCause.NormalClearing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task End_ShouldBeCompleted_WhenTheReloadListsTheConnectedQueuedCallerBeforeTheMemberLeg(int memberState)
    {
        var (state, end, cause) = await ReloadQueuedThenHangUpAsync(StatusEntries(CallerStatus, MemberStatus(memberState)));

        using var scope = new AssertionScope();
        state.Should().Be(CallSessionState.Queued, "premise: the reload opens the call queued");
        end.Should().Be(CallSessionState.Completed,
            "the caller, answered before the reload, hung up with Cause 16 while waiting in the queue");
        cause.Should().Be(HangupCause.NormalClearing);
    }

    [Fact]
    public async Task End_ShouldBeCompleted_WhenTheReloadListsOnlyTheConnectedQueuedCaller()
    {
        var (state, end, cause) = await ReloadQueuedThenHangUpAsync(StatusEntries(CallerStatus), memberArrivesLive: true);

        using var scope = new AssertionScope();
        state.Should().Be(CallSessionState.Queued, "premise: the reload opens the call queued");
        end.Should().Be(CallSessionState.Completed,
            "the caller, answered before the reload, hung up with Cause 16 while waiting in the queue");
        cause.Should().Be(HangupCause.NormalClearing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reload_ShouldOpenTheCallConnected_WhenEveryLegItListsIsUp(bool memberFirst)
    {
        await using var rig = await Rig.StartAsync();
        var entries = memberFirst
            ? StatusEntries(MemberStatus(6), CallerStatus)
            : StatusEntries(CallerStatus, MemberStatus(6));
        await rig.Connection.ReloadAsync(await FramesAsync(StatusReply(entries)), []);

        var session = rig.Manager.GetByChannelId(Caller)
            ?? throw new InvalidOperationException("premise: the reload opens the call");
        using var scope = new AssertionScope();
        session.Participants.Should().HaveCount(2, "premise: both legs are in the call");
        session.State.Should().Be(CallSessionState.Connected, "every leg of the call is up");
        session.ConnectedAt.Should().BeNull("a reload reports that the call is up, never when it answered");
    }

    [Fact]
    public async Task Reload_ShouldPublishNoConnectionAndStampNoConnectedTime_WhenTheCallersUpLegRaisesTheCall()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Connection.ReloadAsync(await FramesAsync(StatusReply(StatusEntries(MemberStatus(5), CallerStatus))),
            await FramesAsync(QueueStatusReply));

        var session = rig.Manager.GetByChannelId(Caller)
            ?? throw new InvalidOperationException("premise: the reload opens the call");
        using var scope = new AssertionScope();
        session.Participants[0].UniqueId.Should().Be(Member, "premise: the member's leg opened the call");
        session.State.Should().Be(CallSessionState.Queued, "premise: the reload reports the call waiting in the queue");
        session.HasBeenConnected.Should().BeTrue("the caller's leg is up: the call was answered before the reload");
        session.ConnectedAt.Should().BeNull("a reload reports that the caller is up, never when it answered");
        rig.Events.OfType<CallConnectedEvent>().Should().BeEmpty("no connection was observed");
        session.Events.Where(e => e.Type == CallSessionEventType.Connected).Should().ContainSingle(
                "the audit trail says once where the connected state came from")
            .Which.Should().Match<CallSessionEvent>(e => e.Detail == "reload" && e.SourceChannel == CallerChannel);
    }

    [Fact]
    public async Task Reload_ShouldLeaveTheCallsStateAlone_WhenItsLegJoinsACallThisManagerWatchedOpen()
    {
        await using var rig = await Rig.StartAsync();
        rig.Deliver(await FramesAsync(MemberCreatedLive));
        var session = rig.Manager.GetByChannelId(Member)
            ?? throw new InvalidOperationException("premise: the live arrival opens the call");
        session.State.Should().Be(CallSessionState.Created, "premise: a live Down leg opens the call created");

        await rig.Connection.ReloadAsync(await FramesAsync(StatusReply(StatusEntries(MemberStatus(0), CallerStatus))), []);

        using var scope = new AssertionScope();
        rig.Manager.GetByChannelId(Caller).Should().BeSameAs(session, "premise: the caller's leg joins the watched call");
        session.State.Should().Be(CallSessionState.Created, "a call this manager watched open keeps the state it watched");
        session.HasBeenConnected.Should().BeFalse();
        session.Events.Should().NotContain(e => e.Type == CallSessionEventType.Connected);
    }

    /// <summary>
    /// The first-attach admission walks the channel table in its own order, which a test cannot choose. Many calls
    /// are admitted at once, so the walk visits a member leg before its caller in some of them; every call must end
    /// completed whichever leg opened it.
    /// </summary>
    [Fact]
    public async Task AdmitExistingCalls_ShouldEndEveryAnsweredCallCompleted_WhenTheWalkVisitsAMemberLegBeforeItsCaller()
    {
        const int calls = 16;
        var entries = new StringBuilder();
        for (var i = 0; i < calls; i++)
        {
            entries.Append(MemberStatus(i % 2 == 0 ? 0 : 5, MemberId(i), MemberChannelOf(i), CallerId(i)));
            entries.Append(CallerStatusOf(CallerId(i), CallerChannelOf(i)));
        }
        var connection = new ReloadableConnection(await FramesAsync(StatusReply(entries.ToString())));
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        try
        {
            await server.StartAsync();
            manager.AttachToServer(server, ServerId);
            await manager.AdmitExistingCalls(ServerId);

            var sessions = Enumerable.Range(0, calls)
                .Select(i => manager.GetByChannelId(CallerId(i))
                    ?? throw new InvalidOperationException($"premise: the admission admits call {i}"))
                .ToList();
            var openedByMember = sessions.Count(s => s.Participants[0].UniqueId.StartsWith("m.", StringComparison.Ordinal));

            for (var i = 0; i < calls; i++)
                foreach (var frame in await FramesAsync(Hangup(MemberId(i), MemberChannelOf(i), CallerId(i), 0)
                    + Hangup(CallerId(i), CallerChannelOf(i), CallerId(i), 16)))
                    connection.Observer.OnNext(frame);

            using var scope = new AssertionScope();
            openedByMember.Should().BePositive("premise: the walk visited a member leg first in at least one call");
            sessions.Should().OnlyContain(s => s.HasBeenConnected,
                "every caller is up when the admission reads the table");
            sessions.Select(s => s.State).Should().OnlyContain(s => s == CallSessionState.Completed,
                "every caller was answered and hung up with Cause 16");
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static string CallerId(int i) => $"c.{i}";
    private static string MemberId(int i) => $"m.{i}";
    private static string CallerChannelOf(int i) => $"PJSIP/loop-{i:x8}";
    private static string MemberChannelOf(int i) => $"PJSIP/agent2-{i + 0x100:x8}";

    private static async Task<(CallSessionState Reloaded, CallSessionState End, HangupCause? Cause)>
        ReloadQueuedThenHangUpAsync(string statusEntries, bool memberArrivesLive = false)
    {
        await using var rig = await Rig.StartAsync();
        await rig.Connection.ReloadAsync(await FramesAsync(StatusReply(statusEntries)), await FramesAsync(QueueStatusReply));
        var session = rig.Manager.GetByChannelId(Caller)
            ?? throw new InvalidOperationException("premise: the reload opens the call");
        var reloaded = session.State;

        if (memberArrivesLive)
            rig.Deliver(await FramesAsync(MemberArrives));
        rig.Deliver(await FramesAsync(Hangups));
        return (reloaded, session.State, session.HangupCause);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly IDisposable _subscription;

        private Rig(ReloadableConnection connection, VerbaraServer server, CallSessionManager manager)
        {
            Connection = connection;
            Server = server;
            Manager = manager;
            _subscription = manager.Events.Subscribe(Events.Add);
        }

        public ReloadableConnection Connection { get; }
        public VerbaraServer Server { get; }
        public CallSessionManager Manager { get; }
        public List<SessionDomainEvent> Events { get; } = [];

        public static async Task<Rig> StartAsync()
        {
            var connection = new ReloadableConnection();
            var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
            var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
                new InMemorySessionStore());
            var rig = new Rig(connection, server, manager);
            manager.AttachToServer(server, ServerId);
            await server.StartAsync();
            return rig;
        }

        public void Deliver(IEnumerable<ManagerEvent> frames)
        {
            foreach (var frame in frames)
                Connection.Observer.OnNext(frame);
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await Manager.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static async Task<IReadOnlyList<ManagerEvent>> FramesAsync(string text) =>
        await AmiCaptureReplay.ReadCaptureAsync(Encoding.ASCII.GetBytes(text.ReplaceLineEndings("\r\n")));

    private static string StatusEntries(params string[] entries) => string.Concat(entries);

    private static string StatusReply(string entries) => $"""
        Response: Success
        ActionID: cap-7
        EventList: start
        Message: Channel status will follow

        {entries}Event: StatusComplete
        ActionID: cap-7
        EventList: Complete
        ListItems: 2
        Items: 2


        """;

    private static readonly string CallerStatus = CallerStatusOf(Caller, CallerChannel);

    private static string CallerStatusOf(string uniqueId, string channel) => $"""
        Event: Status
        Privilege: Call
        Channel: {channel}
        ChannelState: 6
        ChannelStateDesc: Up
        CallerIDNum: 8904
        CallerIDName: Matrix
        Context: capture-sip
        Exten: aq-hangup-in
        Priority: 2
        Uniqueid: {uniqueId}
        Linkedid: {uniqueId}
        Type: PJSIP
        BridgeID:
        Application: Queue
        Data: q-second,r
        Seconds: 3
        ActionID: cap-7


        """;

    private static string MemberStatus(int state) => MemberStatus(state, Member, MemberChannel, Caller);

    private static string MemberStatus(int state, string uniqueId, string channel, string linkedId) => $"""
        Event: Status
        Privilege: Call
        Channel: {channel}
        ChannelState: {state}
        ChannelStateDesc: {state switch { 5 => "Ringing", 6 => "Up", _ => "Down" }}
        CallerIDNum: aq-hangup-in
        Context: capture-sip
        Exten: s
        Priority: 1
        Uniqueid: {uniqueId}
        Linkedid: {linkedId}
        Type: PJSIP
        BridgeID:
        Application: AppQueue
        Data: (Outgoing Line)
        Seconds: 0
        ActionID: cap-7


        """;

    private static string Hangup(string uniqueId, string channel, string linkedId, int cause) => $"""
        Event: Hangup
        Privilege: call,all
        Channel: {channel}
        ChannelState: 6
        ChannelStateDesc: Up
        Uniqueid: {uniqueId}
        Linkedid: {linkedId}
        Cause: {cause}
        Cause-txt: {(cause == 16 ? "Normal Clearing" : "Unknown")}


        """;

    private const string QueueStatusReply = $"""
        Response: Success
        ActionID: cap-8
        EventList: start
        Message: Queue status will follow

        Event: QueueParams
        Queue: q-second
        Max: 0
        Strategy: ringall
        Calls: 1
        ActionID: cap-8

        Event: QueueMember
        Queue: q-second
        Name: PJSIP/agent2
        Location: PJSIP/agent2
        StateInterface: PJSIP/agent2
        Membership: static
        Status: 1
        Paused: 0
        ActionID: cap-8

        Event: QueueEntry
        Queue: q-second
        Position: 1
        Channel: {CallerChannel}
        Uniqueid: {Caller}
        CallerIDNum: 8904
        CallerIDName: Matrix
        Wait: 0
        Priority: 0
        ActionID: cap-8

        Event: QueueStatusComplete
        ActionID: cap-8
        EventList: Complete
        ListItems: 3


        """;

    private const string MemberCreatedLive = $"""
        Event: Newchannel
        Privilege: call,all
        Channel: {MemberChannel}
        ChannelState: 0
        ChannelStateDesc: Down
        CallerIDNum: <unknown>
        Context: capture-sip
        Exten: s
        Priority: 1
        Uniqueid: {Member}
        Linkedid: {Caller}


        """;

    private const string MemberArrives = $"""
        Event: Newchannel
        Privilege: call,all
        Channel: {MemberChannel}
        ChannelState: 0
        ChannelStateDesc: Down
        CallerIDNum: <unknown>
        Context: capture-sip
        Exten: s
        Priority: 1
        Uniqueid: {Member}
        Linkedid: {Caller}

        Event: Newstate
        Privilege: call,all
        Channel: {MemberChannel}
        ChannelState: 5
        ChannelStateDesc: Ringing
        Context: capture-sip
        Exten: second
        Priority: 1
        Uniqueid: {Member}
        Linkedid: {Caller}


        """;

    private const string Hangups = $"""
        Event: QueueCallerAbandon
        Privilege: agent,all
        Channel: {CallerChannel}
        ChannelState: 6
        ChannelStateDesc: Up
        Uniqueid: {Caller}
        Linkedid: {Caller}
        Queue: q-second
        Position: 1
        OriginalPosition: 1
        HoldTime: 1

        Event: Hangup
        Privilege: call,all
        Channel: {MemberChannel}
        ChannelState: 5
        ChannelStateDesc: Ringing
        Uniqueid: {Member}
        Linkedid: {Caller}
        Cause: 0
        Cause-txt: Unknown

        Event: QueueCallerLeave
        Privilege: agent,all
        Channel: {CallerChannel}
        ChannelState: 6
        ChannelStateDesc: Up
        Uniqueid: {Caller}
        Linkedid: {Caller}
        Queue: q-second
        Position: 1
        Count: 0

        Event: Hangup
        Privilege: call,all
        Channel: {CallerChannel}
        ChannelState: 6
        ChannelStateDesc: Up
        Uniqueid: {Caller}
        Linkedid: {Caller}
        Cause: 16
        Cause-txt: Normal Clearing


        """;
}
