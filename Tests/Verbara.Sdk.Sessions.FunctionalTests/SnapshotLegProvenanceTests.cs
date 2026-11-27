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
}
