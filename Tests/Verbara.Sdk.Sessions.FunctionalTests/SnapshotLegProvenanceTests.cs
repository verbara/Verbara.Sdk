using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Which legs may raise a call to the state a snapshot reports, when the call was opened by a leg the manager watched
/// arrive: only a leg the snapshot itself reported, and only to connected. A ringing leg the snapshot reports tells
/// nothing a watched call has not shown, and a leg that arrived live is the manager's to watch.
/// </summary>
public sealed class SnapshotLegProvenanceTests
{
    private const string ServerId = "provenance";
    private const string A = "1795769507.49";
    private const string ACh = "PJSIP/loop-00000031";
    private const string B = "1795769510.52";
    private const string BCh = "PJSIP/agent2-00000034";

    private static async Task<IReadOnlyList<ManagerEvent>> F(string text) =>
        await AmiCaptureReplay.ReadCaptureAsync(Encoding.ASCII.GetBytes(text.ReplaceLineEndings("\r\n")));

    private static string NewChannel(string id, string ch, int state, string linked) => $"""
        Event: Newchannel
        Privilege: call,all
        Channel: {ch}
        ChannelState: {state}
        ChannelStateDesc: {state switch { 4 => "Ring", 5 => "Ringing", 6 => "Up", _ => "Down" }}
        CallerIDNum: 8904
        CallerIDName: Matrix
        Context: capture-sip
        Exten: aq-hangup-in
        Priority: 1
        Uniqueid: {id}
        Linkedid: {linked}


        """;

    private static string NewState(string id, string ch, int state, string linked) => $"""
        Event: Newstate
        Privilege: call,all
        Channel: {ch}
        ChannelState: {state}
        ChannelStateDesc: {state switch { 4 => "Ring", 5 => "Ringing", 6 => "Up", _ => "Down" }}
        CallerIDNum: 8904
        CallerIDName: Matrix
        Context: capture-sip
        Exten: aq-hangup-in
        Priority: 1
        Uniqueid: {id}
        Linkedid: {linked}


        """;

    private static string Hangup(string id, string ch, string linked, int cause) => $"""
        Event: Hangup
        Privilege: call,all
        Channel: {ch}
        ChannelState: 6
        ChannelStateDesc: Up
        Uniqueid: {id}
        Linkedid: {linked}
        Cause: {cause}
        Cause-txt: {(cause == 16 ? "Normal Clearing" : "Unknown")}


        """;

    private static string StatusEntry(string id, string ch, int state, string linked) => $"""
        Event: Status
        Privilege: Call
        Channel: {ch}
        ChannelState: {state}
        ChannelStateDesc: {state switch { 4 => "Ring", 5 => "Ringing", 6 => "Up", _ => "Down" }}
        CallerIDNum: 8904
        CallerIDName: Matrix
        Context: capture-sip
        Exten: aq-hangup-in
        Priority: 2
        Uniqueid: {id}
        Linkedid: {linked}
        Type: PJSIP
        BridgeID:
        Application: Dial
        Data: x
        Seconds: 3
        ActionID: cap-7


        """;

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

    /// <summary>
    /// A call watched open and answered by its own dialplan; a second leg arrives live after the attach, and the
    /// first-attach admission walk reaches it in the channel table before the manager has handled its arrival (a
    /// channel subscriber registered before the manager holds the window open). The walk must not raise the call
    /// from that leg's live state.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task AdmitExistingCalls_ShouldNotRaiseAWatchedCall_WhenTheWalkReachesALegThatArrivedLive(int memberState)
    {
        var connection = new ReloadableConnection();
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        var events = new List<SessionDomainEvent>();
        using var sub = manager.Events.Subscribe(events.Add);
        var memberStateChange = await F(NewState(B, BCh, memberState, A));
        Exception? walkError = null;
        var walked = false;
        void OnAdded(AsteriskChannel c)
        {
            if (walked || c.UniqueId != B) return;
            walked = true;
            foreach (var f in memberStateChange) connection.Observer.OnNext(f);
            try
            {
                manager.AdmitExistingCalls(ServerId).AsTask().Wait(TimeSpan.FromSeconds(10))
                    .Should().BeTrue("premise: the admission walk ends");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { walkError = ex; }
        }
        try
        {
            await server.StartAsync();
            server.Channels.ChannelAdded += OnAdded;
            manager.AttachToServer(server, ServerId);
            foreach (var f in await F(NewChannel(A, ACh, 4, A) + NewState(A, ACh, 6, A)))
                connection.Observer.OnNext(f);
            var session = manager.GetByChannelId(A) ?? throw new InvalidOperationException("premise: A opens the call");
            foreach (var f in await F(NewChannel(B, BCh, 0, A)))
                connection.Observer.OnNext(f);

            using var scope = new AssertionScope();
            walked.Should().BeTrue("premise: the walk ran inside the member leg's arrival");
            walkError.Should().BeNull();
            session.State.Should().Be(CallSessionState.Created, "a leg that arrived live never raises a watched call");
            session.HasBeenConnected.Should().BeFalse();
            events.OfType<CallConnectedEvent>().Should().BeEmpty();
        }
        finally
        {
            server.Channels.ChannelAdded -= OnAdded;
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// A call watched open and answered by its own dialplan, with no dial and no queue; a reload relists its leg up and
    /// reports a new leg of it. A ringing new leg leaves the call as watched, so it still ends completed from the answer
    /// it observed; an up new leg connects it silently, with no connection published and no time stamped.
    /// </summary>
    [Theory]
    [InlineData(5, CallSessionState.Created, false)]
    [InlineData(6, CallSessionState.Connected, true)]
    public async Task Reload_ShouldRaiseAWatchedCallOnlyToConnected_WhenANewLegJoinsItFromTheSnapshot(
        int memberState, CallSessionState expected, bool connected)
    {
        var connection = new ReloadableConnection();
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        var events = new List<SessionDomainEvent>();
        using var sub = manager.Events.Subscribe(events.Add);
        try
        {
            manager.AttachToServer(server, ServerId);
            await server.StartAsync();
            foreach (var f in await F(NewChannel(A, ACh, 4, A) + NewState(A, ACh, 6, A)))
                connection.Observer.OnNext(f);
            var session = manager.GetByChannelId(A) ?? throw new InvalidOperationException("premise: A opens the call");
            session.State.Should().Be(CallSessionState.Created, "premise: a dialplan answer alone connects nothing");

            await connection.ReloadAsync(
                await F(StatusReply(StatusEntry(A, ACh, 6, A) + StatusEntry(B, BCh, memberState, A))), []);
            var reloaded = (session.State, session.HasBeenConnected);
            foreach (var f in await F(Hangup(B, BCh, A, 16) + Hangup(A, ACh, A, 16)))
                connection.Observer.OnNext(f);

            using var scope = new AssertionScope();
            reloaded.State.Should().Be(expected);
            reloaded.HasBeenConnected.Should().Be(connected);
            session.State.Should().Be(CallSessionState.Completed, "the caller hung up normally after an answer");
            events.OfType<CallConnectedEvent>().Should().BeEmpty("the snapshot reported the state; nothing was observed");
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static string CallerId(int i, int set) => $"c{set}.{i}";
    private static string MemberId(int i, int set) => $"m{set}.{i}";
    private static string CallerCh(int i) => $"PJSIP/loop-{i:x8}";
    private static string MemberCh(int i) => $"PJSIP/agent2-{i + 0x100:x8}";

    /// <summary>
    /// Both legs come from the start-up snapshot, the caller ringing and the member down, and the caller is answered
    /// live before the manager attaches, so no session sees that answer. When the admission walks the table the call is
    /// up: every call is admitted connected and ends completed, whichever leg the walk visits first.
    /// </summary>
    [Fact]
    public async Task AdmitExistingCalls_ShouldConnectEveryCall_WhenItsCallerWasAnsweredAfterTheSnapshotBeforeTheAttach()
    {
        const int calls = 16;
        var byOpener = new List<(bool MemberOpened, CallSessionState Admitted, CallSessionState End)>();
        var entries = new StringBuilder();
        for (var set = 0; set < 32 && !byOpener.Any(x => x.MemberOpened); set++)
        {
            entries.Clear();
            for (var i = 0; i < calls; i++)
            {
                entries.Append(StatusEntry(MemberId(i, set), MemberCh(i), 0, CallerId(i, set)));
                entries.Append(StatusEntry(CallerId(i, set), CallerCh(i), 4, CallerId(i, set)));
            }
            var connection = new ReloadableConnection(await F(StatusReply(entries.ToString())));
            var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
            var manager = new CallSessionManager(Options.Create(new SessionOptions()),
                NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
            try
            {
                await server.StartAsync();
                for (var i = 0; i < calls; i++)
                    foreach (var f in await F(NewState(CallerId(i, set), CallerCh(i), 6, CallerId(i, set))))
                        connection.Observer.OnNext(f);
                server.Channels.GetByUniqueId(CallerId(0, set))!.State.Should().Be(ChannelState.Up, "premise");
                manager.AttachToServer(server, ServerId);
                await manager.AdmitExistingCalls(ServerId);
                var sessions = Enumerable.Range(0, calls).Select(i => manager.GetByChannelId(CallerId(i, set))!).ToList();
                var admitted = sessions.Select(s => s.State).ToList();
                for (var i = 0; i < calls; i++)
                    foreach (var f in await F(Hangup(MemberId(i, set), MemberCh(i), CallerId(i, set), 0)
                        + Hangup(CallerId(i, set), CallerCh(i), CallerId(i, set), 16)))
                        connection.Observer.OnNext(f);
                for (var i = 0; i < calls; i++)
                    byOpener.Add((sessions[i].Participants[0].UniqueId.StartsWith('m'), admitted[i], sessions[i].State));
            }
            finally
            {
                await manager.DisposeAsync();
                await server.DisposeAsync();
            }
        }

        using var scope = new AssertionScope();
        byOpener.Should().Contain(x => x.MemberOpened, "premise: some call was opened from its member leg");
        byOpener.Select(x => x.Admitted).Should().OnlyContain(s => s == CallSessionState.Connected,
            "the caller is up when the walk reads the table");
        byOpener.Select(x => x.End).Should().OnlyContain(s => s == CallSessionState.Completed);
    }

    /// <summary>
    /// The caller's leg comes from the start-up snapshot ringing and is answered before the attach; the member's leg
    /// opens the call live after it, and the admission walk joins the caller's leg: the call is admitted connected and
    /// ends completed.
    /// </summary>
    [Fact]
    public async Task AdmitExistingCalls_ShouldEndCompleted_WhenTheMemberOpensLiveAndTheCallerWasAnsweredAfterTheSnapshot()
    {
        const string c = "1795769507.49", cch = "PJSIP/loop-00000031", m = "1795769510.52", mch = "PJSIP/agent2-00000034";
        var connection = new ReloadableConnection(await F(StatusReply(StatusEntry(c, cch, 4, c))));
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        try
        {
            await server.StartAsync();
            foreach (var f in await F(NewState(c, cch, 6, c))) connection.Observer.OnNext(f);
            manager.AttachToServer(server, ServerId);
            foreach (var f in await F(NewChannel(m, mch, 0, c))) connection.Observer.OnNext(f);
            await manager.AdmitExistingCalls(ServerId);
            var session = manager.GetByChannelId(c)!;
            var admitted = session.State;
            var opener = session.Participants[0].UniqueId;
            foreach (var f in await F(Hangup(m, mch, c, 0) + Hangup(c, cch, c, 16))) connection.Observer.OnNext(f);

            using var scope = new AssertionScope();
            opener.Should().Be(m, "premise");
            admitted.Should().Be(CallSessionState.Connected, "the caller is up when the walk reads the table");
            session.State.Should().Be(CallSessionState.Completed);
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
