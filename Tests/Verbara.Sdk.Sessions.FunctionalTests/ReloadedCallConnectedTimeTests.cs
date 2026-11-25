using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The connected time of a call a reload opened connected: a return from hold sets none, and the first connection the
/// SDK observes for it sets it, to that connection's announcement.
/// </summary>
/// <remarks>
/// The frames are written here, shaped on the <c>Status</c>, <c>Hold</c>, <c>Unhold</c>, <c>QueueCallerJoin</c> and
/// <c>AgentConnect</c> frames of the connected-signal-matrix captures, and parsed by the SDK's own reader: no capture
/// holds a reloaded call that is held, resumed and then queued.
/// </remarks>
public sealed class ReloadedCallConnectedTimeTests
{
    private const string Caller = "1795406585.101";
    private const string CallerChannel = "PJSIP/loop-00000047";
    private const string MemberLeg = "1795406599.110";
    private const string MemberChannel = "PJSIP/agent1-00000050";

    [Fact]
    public async Task ConnectedAt_ShouldStayNullUntilAMemberTakesIt_WhenAReloadedCallIsHeldResumedAndThenQueued()
    {
        var connection = new ReloadableConnection();
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        var events = new List<SessionDomainEvent>();
        try
        {
            using var subscription = manager.Events.Subscribe(events.Add);
            manager.AttachToServer(server, "reload-replay");
            await server.StartAsync();
            await connection.ReloadAsync(await FramesAsync(StatusReply), []);
            var session = manager.GetByChannelId(Caller)
                ?? throw new InvalidOperationException("premise: the reload opens the call");
            session.State.Should().Be(CallSessionState.Connected, "premise: the reload opens the call connected");

            foreach (var frame in await FramesAsync(HoldAndResume))
                connection.Observer.OnNext(frame);
            var afterResume = (session.State, session.ConnectedAt, Connected: events.OfType<CallConnectedEvent>().Count());

            foreach (var frame in await FramesAsync(QueuedAndTaken))
                connection.Observer.OnNext(frame);
            var connected = events.OfType<CallConnectedEvent>().ToList();

            using var scope = new AssertionScope();
            events.OfType<CallHeldEvent>().Should().ContainSingle("the hold is reported");
            events.OfType<CallResumedEvent>().Should().ContainSingle("the return from hold is reported");
            afterResume.State.Should().Be(CallSessionState.Connected, "premise: the call is off hold");
            afterResume.Connected.Should().Be(0, "a return from hold is not an answer");
            afterResume.ConnectedAt.Should().BeNull(
                "the return from hold of a call a reload opened sets no connected time: its answer was never observed");
            connected.Should().ContainSingle("the member's connection is the call's first observed connection");
            session.ConnectedAt.Should().Be(connected.Single().Timestamp,
                "the call's connected time is that connection's announcement, not the instant it was taken off hold");
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static async Task<IReadOnlyList<ManagerEvent>> FramesAsync(string text) =>
        await AmiCaptureReplay.ReadCaptureAsync(Encoding.ASCII.GetBytes(text.ReplaceLineEndings("\r\n")));

    private const string Party = $"""
        ChannelState: 6
        ChannelStateDesc: Up
        CallerIDNum: 8601
        CallerIDName: Matrix
        Context: capture-sip
        Exten: hold-in
        Priority: 1
        Uniqueid: {Caller}
        Linkedid: {Caller}
        """;

    private const string StatusReply = $"""
        Event: Status
        Privilege: Call
        Channel: {CallerChannel}
        {Party}
        Type: PJSIP
        ActionID: reload-status

        Event: StatusComplete
        ActionID: reload-status
        EventList: Complete
        ListItems: 1
        Items: 1


        """;

    private const string HoldAndResume = $"""
        Event: Hold
        Privilege: call,all
        Channel: {CallerChannel}
        {Party}
        MusicClass: default

        Event: Unhold
        Privilege: call,all
        Channel: {CallerChannel}
        {Party}


        """;

    private const string QueuedAndTaken = $"""
        Event: QueueCallerJoin
        Privilege: agent,all
        Channel: {CallerChannel}
        {Party}
        Queue: q-second
        Position: 1
        Count: 1

        Event: Newchannel
        Privilege: call,all
        Channel: {MemberChannel}
        ChannelState: 0
        ChannelStateDesc: Down
        CallerIDNum: 8601
        Context: capture-agents
        Exten: s
        Priority: 1
        Uniqueid: {MemberLeg}
        Linkedid: {Caller}

        Event: Newstate
        Privilege: call,all
        Channel: {MemberChannel}
        ChannelState: 6
        ChannelStateDesc: Up
        Uniqueid: {MemberLeg}
        Linkedid: {Caller}

        Event: QueueCallerLeave
        Privilege: agent,all
        Channel: {CallerChannel}
        {Party}
        Queue: q-second
        Position: 1
        Count: 0

        Event: AgentConnect
        Privilege: agent,all
        Channel: {CallerChannel}
        {Party}
        DestChannel: {MemberChannel}
        DestUniqueid: {MemberLeg}
        DestLinkedid: {Caller}
        Queue: q-second
        Interface: PJSIP/agent1
        MemberName: PJSIP/agent1
        HoldTime: 2
        RingTime: 1


        """;
}
