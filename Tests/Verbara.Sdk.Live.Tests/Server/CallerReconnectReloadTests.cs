using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A session the caller's own <c>ConnectAsync</c> brings back after a loss reloads a started live server, as a session
/// the reconnect loop brings back does, while the public <see cref="IAmiConnection.Reconnected"/> stays what it is: raised
/// by the loop alone. The first connect stays the start's, a connect that fails recovers nothing, and each successful
/// session has one automatic reload, on that session and no other.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="AmiConnection"/> over <see cref="Run"/>, with a session manager attached to the server. A test that
/// takes a <see cref="Route"/> runs one body over both routes, so the row for the reconnect loop is the control of the
/// row for the caller: the same peers, the same observation, only the route differs.
/// </para>
/// <para>
/// Nothing here waits on the wall clock. Every wait ends on a signal, and <see cref="Run.Bound"/> is a hang bound. An
/// absence is ordered, not observed: <see cref="Rig.SettleAsync"/> drains the connection's notification queue, waits for
/// every load to end and then round-trips a <c>Ping</c> on the live session, so an action sent before it has been counted.
/// </para>
/// <para>
/// The queue counters are read with <see cref="LiveCounters"/>, so the class runs in <see cref="LiveCountersGroup"/>.
/// </para>
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class CallerReconnectReloadTests
{
    /// <summary>Who brings the session back.</summary>
    public enum Route
    {
        /// <summary>Automatic reconnection off: the caller's <c>ConnectAsync</c>.</summary>
        Caller,

        /// <summary>Automatic reconnection on: the reconnect loop, the control.</summary>
        Loop,
    }

    private const string StateLoaded = "[LIVE] State loaded";
    private const string ReloadStarted = "[LIVE] Reconnected: reloading state";
    private const string Connected = "[AMI] Connected";
    private const string Room = "capture-room";
    private const string RoomBridge = "22222222-2222-2222-2222-222222222222";
    private const string GhostBridge = "33333333-3333-3333-3333-333333333333";
    private const string ReloadFailed = "[LIVE] Reconnect reload failed";

    private static readonly Leg Stayed = new("1790700000.1", "PJSIP/stayed-00000001");
    private static readonly Leg Gone = new("1790700000.2", "PJSIP/gone-00000002");
    private static readonly Leg Started = new("1790700000.3", "PJSIP/started-00000003");
    private static readonly Leg RoomStayed = new("1790700000.4", "Local/conf@capture-00000004;2");
    private static readonly Leg RoomGone = new("1790700000.5", "Local/conf@capture-00000005;2");
    private static readonly Leg RoomStarted = new("1790700000.6", "Local/conf@capture-00000006;2");
    private static readonly Leg Sentinel = new("1790700000.7", "PJSIP/sentinel-00000007");
    private static readonly Leg Ghost = new("1790700000.8", "PJSIP/ghost-00000008");
    private static readonly Leg RoomGhost = new("1790700000.9", "Local/conf@capture-00000009;2");

    /// <summary>What Live holds before the outage.</summary>
    private static readonly Tables BeforeTables = new(
        Sorted(Stayed.Uid, Gone.Uid, RoomStayed.Uid, RoomGone.Uid), Sorted(Stayed.Channel, Gone.Channel),
        Sorted(RoomStayed.Channel, RoomGone.Channel));

    /// <summary>What Asterisk reports after it.</summary>
    private static readonly Tables AfterTables = new(
        Sorted(Stayed.Uid, Started.Uid, RoomStayed.Uid, RoomStarted.Uid), Sorted(Stayed.Channel, Started.Channel),
        Sorted(RoomStayed.Channel, RoomStarted.Channel));

    // ---- The route itself ----

    [Theory]
    [InlineData(Route.Caller)]
    [InlineData(Route.Loop)]
    public async Task Reload_ShouldMatchWhatAsteriskReportsAndEndWhatIsGoneOnce_WhenALostSessionComesBack(Route route)
    {
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), second, route);
        var stayedEntry = rig.Entry(Stayed);

        await rig.LoseAndComeBackAsync(route);
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();
        await rig.ObservedCallAsync(second);

        using (new AssertionScope())
        {
            new
            {
                Reloaded = reloaded,
                StatusAsked = second.Asked("Status"),
                QueueStatusAsked = second.Asked("QueueStatus"),
                Tables = rig.Tables(),
                rig.Counters.Joined,
                rig.Counters.Left,
                Removed = rig.Removed,
                GoneEnding = rig.Ending(Gone),
                State = rig.Run.Connection.State,
                ReconnectedRaised = rig.Run.Reconnected.IsCompleted,
                PeerFault = second.Fault?.Message,
            }.Should().BeEquivalentTo(
                new
                {
                    Reloaded = true,
                    StatusAsked = 1,
                    QueueStatusAsked = 1,
                    Tables = AfterTables,
                    Joined = 1L,
                    Left = 1L,
                    Removed = new[] { Gone.Uid, RoomGone.Uid, Sentinel.Uid },
                    GoneEnding = new Ending("reload", null, 1),
                    State = AmiConnectionState.Connected,
                    ReconnectedRaised = route == Route.Loop,
                    PeerFault = default(string),
                },
                "a session that comes back after a loss runs one full reload on that session, with no second start and no "
                + "explicit request; only the reconnect loop raises the public Reconnected");
            rig.Entry(Stayed).Should().BeSameAs(stayedEntry, "the visit that survived the outage is the same visit");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldStillMatchWhatAsteriskReports_WhenAHostKeepsItsCallAfterReconnecting()
    {
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), second, Route.Caller);

        await rig.LoseAndComeBackAsync(Route.Caller);
        await rig.Run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        await rig.SettleAsync();

        new { Tables = rig.Tables(), rig.Counters.Left, PeerFault = second.Fault?.Message }.Should().BeEquivalentTo(
            new { Tables = AfterTables, Left = 1L, PeerFault = default(string) },
            "the explicit request the migration guide documents stays supported, and it proves the peer and the new "
            + "session serve a load when one is asked for");
    }

    [Fact]
    public async Task Reload_ShouldWaitForASlowConnectionLostHandler_WhenTheCallerReconnects()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(first, second, Route.Caller);
        using var hold = new HandlerHold();
        rig.Run.Server.ConnectionLost += _ => hold.Block();

        first.CloseSession();
        await hold.Held.WaitAsync(Run.Bound);
        rig.Run.ReleaseSecondPeer();
        await rig.CallerConnectsAsync();
        await rig.PingAsync();
        var statusAskedWhileHeld = second.Asked("Status");
        var pendingWhileHeld = rig.Run.Server.IsReconnectPending;

        hold.Release();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        new
        {
            StatusAskedWhileHeld = statusAskedWhileHeld,
            PendingWhileHeld = pendingWhileHeld,
            Reloaded = reloaded,
            StatusAsked = second.Asked("Status"),
            PendingAfter = rig.Run.Server.IsReconnectPending,
        }.Should().BeEquivalentTo(
            new { StatusAskedWhileHeld = 0, PendingWhileHeld = true, Reloaded = true, StatusAsked = 1, PendingAfter = false },
            "a loss's handlers have returned before the reload of the session that follows it, on this route as on the "
            + "loop's, the sweep stands back from the moment of the login, and the reload that waited for them still runs, once");
    }

    [Fact]
    public async Task Reload_ShouldRun_WhenALossHandlerWaitsForTheCallersConnect()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(first, second, Route.Caller);
        Exception? connectOutcome = null;
        rig.Run.ReleaseSecondPeer();
        rig.Run.Server.ConnectionLost += _ =>
        {
            // Holds the notification queue for as long as the connect runs: the connect must not wait for that queue.
#pragma warning disable VSTHRD002 // a handler that blocks is the case under test
            connectOutcome = Record.Exception(() => rig.Run.Connection.ConnectAsync().AsTask().Wait(Run.Bound));
#pragma warning restore VSTHRD002
        };

        first.CloseSession();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        new { ConnectOutcome = connectOutcome?.Message, Reloaded = reloaded, StatusAsked = second.Asked("Status") }
            .Should().BeEquivalentTo(
                new { ConnectOutcome = default(string), Reloaded = true, StatusAsked = 1 },
                "the connect finishes under the handler that waits for it, and the reload follows once that handler returns");
    }

    // ---- The first connect stays the start's ----

    [Fact]
    public async Task StartAsync_ShouldRunOneLoad_WhenTheFirstConnectPrecedesIt()
    {
        var peer = PeerBefore();
        await using var run = await Run.ConnectAsync(peer);

        await run.StartServerAsync();
        await SettleAsync(run);

        new { StatusAsked = peer.Asked("Status"), Loaded = Lines(run, StateLoaded) }.Should().BeEquivalentTo(
            new { StatusAsked = 1, Loaded = 1 }, "the first session has nothing to reload: the start loads it, once");
    }

    [Fact]
    public async Task StartAsync_ShouldRunOneLoad_WhenTheCallerReconnectedBeforeItStarted()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var run = await Run.ConnectAsync(first, second);

        first.CloseSession();
        AwaitState(run, s => s != AmiConnectionState.Connected);
        run.ReleaseSecondPeer();
        await run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);
        await run.StartServerAsync();
        await SettleAsync(run);

        new { FirstAsked = first.Asked("Status"), SecondAsked = second.Asked("Status"), Loaded = Lines(run, StateLoaded) }
            .Should().BeEquivalentTo(
                new { FirstAsked = 0, SecondAsked = 1, Loaded = 1 },
                "a loss no started server saw is not an outage of that server: its start loads the session it finds, once");
    }

    [Fact]
    public async Task ConnectAsync_ShouldLoadNothing_WhenTheFirstConnectFollowsAStartThatFoundNoConnection()
    {
        var peer = PeerBefore();
        await using var run = Run.Unconnected(peer);

        var startOutcome = await Record.ExceptionAsync(run.StartServerAsync);
        await run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);
        await SettleAsync(run);

        new { Start = startOutcome?.GetType(), StatusAsked = peer.Asked("Status"), Loaded = Lines(run, StateLoaded) }
            .Should().BeEquivalentTo(
                new { Start = typeof(AmiNotConnectedException), StatusAsked = 0, Loaded = 0 },
                "nothing was lost: a first connect is not a reconnect, and the start that failed told its host so");
    }

    // ---- A connect that fails recovers nothing ----

    [Fact]
    public async Task ConnectAsync_ShouldLeaveLiveAsItWas_WhenTheCallersConnectFails()
    {
        var first = PeerBefore();
        var refusing = new BootingAsterisk { RefusesLogin = true };
        await using var rig = await Rig.StartAsync(first, refusing, Route.Caller);

        first.CloseSession();
        rig.Run.ReleaseSecondPeer();
        AwaitState(rig.Run, s => s != AmiConnectionState.Connected);
        var outcome = await rig.CallerConnectsCapturingAsync();
        await rig.SettleAsync();

        new
        {
            Outcome = outcome?.GetType(),
            LoginAsked = refusing.Asked("Login"),
            Tables = rig.Tables(),
            Removed = rig.Removed,
            Loaded = Lines(rig.Run, StateLoaded),
            ReloadsStarted = Lines(rig.Run, ReloadStarted),
            State = rig.Run.Connection.State,
        }.Should().BeEquivalentTo(
            new
            {
                Outcome = typeof(AmiAuthenticationException),
                LoginAsked = 1,
                Tables = BeforeTables,
                Removed = Array.Empty<string>(),
                Loaded = 1,
                ReloadsStarted = 0,
                State = AmiConnectionState.Disconnected,
            },
            "a connect that never logged in has no session to load: Live keeps what it held and reports no recovery");
    }

    [Fact]
    public async Task ConnectAsync_ShouldReloadOnce_WhenItSucceedsAfterAConnectThatFailed()
    {
        var first = PeerBefore();
        var refusing = new BootingAsterisk { RefusesLogin = true };
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(first, refusing, Route.Caller, laterPeers: [third]);

        first.CloseSession();
        rig.Run.ReleaseSecondPeer();
        AwaitState(rig.Run, s => s != AmiConnectionState.Connected);
        var refused = await rig.CallerConnectsCapturingAsync();
        await rig.CallerConnectsAsync();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        new
        {
            Refused = refused?.GetType(),
            Reloaded = reloaded,
            StatusAsked = third.Asked("Status"),
            Tables = rig.Tables(),
        }.Should().BeEquivalentTo(
            new { Refused = typeof(AmiAuthenticationException), Reloaded = true, StatusAsked = 1, Tables = AfterTables },
            "the outage stays open across a failed connect, and the session that finally logs in closes it");
    }

    [Fact]
    public async Task Reload_ShouldEndNoCallAndReportNoRecovery_WhenTheNewSessionEndsDuringIt()
    {
        var first = PeerBefore();
        var ending = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [Status(Started), Status(Stayed)],
            Close = PeerClose.DuringStatus,
            StatusChannelsBeforeClose = 1,
        };
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(first, ending, Route.Caller, laterPeers: [third]);

        await rig.LoseAndComeBackAsync(Route.Caller);
        var asked = await CompletesWithinBoundAsync(ending.AskedAtLeast("Status", 1));
        AwaitState(rig.Run, s => s != AmiConnectionState.Connected, asked);
        await rig.SettleAsync();
        var afterTheCut = new { Tables = rig.Tables(), Removed = rig.Removed, Loaded = Lines(rig.Run, StateLoaded) };

        await rig.CallerConnectsAfterAsync(asked);
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        using (new AssertionScope())
        {
            asked.Should().BeTrue("the session logged in, so its reload asks for the channels before the peer cuts it");
            afterTheCut.Should().BeEquivalentTo(
                new { Tables = BeforeTables, Removed = Array.Empty<string>(), Loaded = 1 },
                "a snapshot cut short removes nothing, and the load it belonged to is not reported as a recovery");
            new { Reloaded = reloaded, Tables = rig.Tables() }.Should().BeEquivalentTo(
                new { Reloaded = true, Tables = AfterTables }, "the next session that logs in still reloads");
        }
    }

    // ---- The caller and the reconnect loop ----

    [Fact]
    public async Task ConnectAsync_ShouldBeRefusedAndLeaveOneReload_WhileTheReconnectLoopOwnsTheSession()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(first, second, Route.Loop);

        first.CloseSession();
        await rig.Run.SecondSocketCreated.WaitAsync(Run.Bound);
        var whileConnecting = await rig.CallerConnectsCapturingAsync();
        rig.Run.ReleaseSecondPeer();
        await rig.Run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        var onceConnected = await rig.CallerConnectsCapturingAsync();
        await rig.SettleAsync();

        new
        {
            WhileConnecting = whileConnecting?.GetType(),
            OnceConnected = onceConnected?.GetType(),
            StatusAsked = second.Asked("Status"),
            Loaded = Lines(rig.Run, StateLoaded),
        }.Should().BeEquivalentTo(
            new
            {
                WhileConnecting = typeof(InvalidOperationException),
                OnceConnected = typeof(InvalidOperationException),
                StatusAsked = 1,
                Loaded = 2,
            },
            "a connect refused for a live session starts no session of its own, so it adds no reload to the loop's");
    }

    [Fact]
    public async Task ConnectAsync_ShouldReload_WhenTheCallerReconnectsAfterTheLoopGaveUp()
    {
        var first = PeerBefore();
        var refusing = new BootingAsterisk { RefusesLogin = true };
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(first, refusing, Route.Loop, maxReconnectAttempts: 1, laterPeers: [third]);

        first.CloseSession();
        rig.Run.ReleaseSecondPeer();
        AwaitState(rig.Run, s => s == AmiConnectionState.Disconnected);
        await rig.CallerConnectsAsync();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        new
        {
            Reloaded = reloaded,
            StatusAsked = third.Asked("Status"),
            Tables = rig.Tables(),
            ReconnectedRaised = rig.Run.Reconnected.IsCompleted,
        }.Should().BeEquivalentTo(
            new { Reloaded = true, StatusAsked = 1, Tables = AfterTables, ReconnectedRaised = false },
            "with automatic reconnection on, a loop that gave up hands the session to the caller, whose connect reloads Live");
    }

    [Fact]
    public async Task Reload_ShouldRunOnceOnTheCallersSession_WhenTheLoopsReconnectedArrivesStaleBehindIt()
    {
        var first = PeerBefore();
        var loops = PeerAfter();
        var refusing = new BootingAsterisk { RefusesLogin = true };
        var callers = PeerAfter();
        await using var rig = await Rig.StartAsync(first, loops, Route.Loop, maxReconnectAttempts: 1,
            laterPeers: [refusing, callers]);
        using var hold = new HandlerHold();
        rig.Run.Server.ConnectionLost += _ => hold.Block();

        // The loop's session logs in behind the held loss, so its Reconnected waits on the queue while that session ends
        // and the next loop gives up.
        first.CloseSession();
        await hold.Held.WaitAsync(Run.Bound);
        rig.Run.ReleaseSecondPeer();
        await rig.Run.ConnectionLog.Logged(Connected, 2).WaitAsync(Run.Bound);
        loops.CloseSession();
        AwaitState(rig.Run, s => s == AmiConnectionState.Disconnected);
        await rig.CallerConnectsAsync();
        hold.Release();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        new
        {
            Reloaded = reloaded,
            RetiredAsked = loops.Asked("Status"),
            CallersAsked = callers.Asked("Status"),
            Loaded = Lines(rig.Run, StateLoaded),
            ReconnectedRaised = rig.Run.Reconnected.IsCompleted,
        }.Should().BeEquivalentTo(
            new { Reloaded = true, RetiredAsked = 0, CallersAsked = 1, Loaded = 2, ReconnectedRaised = false },
            "the Reconnected of a session no longer current is not delivered, and it does not stand in for the reload of the "
            + "session that is");
    }

    // ---- A loss or a disposal retires the reload ----

    [Fact]
    public async Task Reload_ShouldNotStart_WhenTheServerIsDisposedWhileItWaitsBehindTheLoss()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(first, second, Route.Caller);
        using var hold = new HandlerHold();
        rig.Run.Server.ConnectionLost += _ => hold.Block();

        first.CloseSession();
        await hold.Held.WaitAsync(Run.Bound);
        rig.Run.ReleaseSecondPeer();
        await rig.CallerConnectsAsync();
        await rig.Run.Server.DisposeAsync().AsTask().WaitAsync(Run.Bound);
        hold.Release();
        await rig.SettleAsync();

        new
        {
            State = rig.Run.Connection.State,
            StatusAsked = second.Asked("Status"),
            Loaded = Lines(rig.Run, StateLoaded),
            Tables = rig.Tables(),
        }.Should().BeEquivalentTo(
            new { State = AmiConnectionState.Connected, StatusAsked = 0, Loaded = 1, Tables = BeforeTables },
            "the session is up and only the disposal stands in the way: a disposed server loads nothing");
    }

    [Fact]
    public async Task Reload_ShouldApplyNothing_WhenTheServerIsDisposedDuringIt()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = PeerAfter();
        second.StatusAnsweredAfter = answer.Task;
        await using var rig = await Rig.StartAsync(PeerBefore(), second, Route.Caller);
        try
        {
            await rig.LoseAndComeBackAsync(Route.Caller);
            var asked = await CompletesWithinBoundAsync(second.AskedAtLeast("Status", 1));
            await rig.Run.Server.DisposeAsync().AsTask().WaitAsync(Run.Bound);
            answer.TrySetResult();
            await rig.SettleAsync();

            new { Asked = asked, Tables = rig.Tables(), Removed = rig.Removed, Loaded = Lines(rig.Run, StateLoaded) }
                .Should().BeEquivalentTo(
                    new { Asked = true, Tables = BeforeTables, Removed = Array.Empty<string>(), Loaded = 1 },
                    "the disposal reached the reload while it read the channels: nothing is reconciled into a disposed server");
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task Reload_ShouldRunOnlyOnTheLatestSession_WhenASecondLossRetiresTheOneWaiting()
    {
        var first = PeerBefore();
        var retired = PeerAfter();
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(first, retired, Route.Caller, laterPeers: [third]);
        using var hold = new HandlerHold();
        rig.Run.Server.ConnectionLost += _ => hold.Block();

        first.CloseSession();
        await hold.Held.WaitAsync(Run.Bound);
        rig.Run.ReleaseSecondPeer();
        await rig.CallerConnectsAsync();
        retired.CloseSession();
        AwaitState(rig.Run, s => s != AmiConnectionState.Connected);
        await rig.CallerConnectsAsync();
        hold.Release();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        new
        {
            Reloaded = reloaded,
            RetiredAsked = retired.Asked("Status"),
            LatestAsked = third.Asked("Status"),
            Loaded = Lines(rig.Run, StateLoaded),
        }.Should().BeEquivalentTo(
            new { Reloaded = true, RetiredAsked = 0, LatestAsked = 1, Loaded = 2 },
            "a session lost before its reload could start never loads, and the session after it reloads once");
    }

    [Theory]
    [InlineData(Route.Caller)]
    [InlineData(Route.Loop)]
    public async Task RetiredReload_ShouldApplyNothingToItsSuccessor_WhenItsSessionIsLostDuringIt(Route route)
    {
        var retired = PeerAfter();
        retired.QueueStatusStopsAfterMember = true;
        retired.QueueMemberPaused = true;
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), retired, route, laterPeers: [third]);
        using var hold = new HandlerHold();
        rig.Run.Server.QueueRowBuffered = hold.Block;

        await rig.LoseAndComeBackAsync(route);
        var held = await CompletesWithinBoundAsync(hold.Held);
        if (held)
        {
            retired.CloseSession();
            if (route == Route.Caller)
            {
                AwaitState(rig.Run, s => s != AmiConnectionState.Connected);
                await rig.CallerConnectsAsync();
            }

            await rig.Run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        }

        hold.Release();
        await rig.SettleAsync();

        new
        {
            Held = held,
            Loaded = Lines(rig.Run, StateLoaded),
            LatestAsked = third.Asked("Status"),
            MemberPaused = rig.Run.Server.Queues.GetByName(BootingAsterisk.QueueName)!
                .Members[BootingAsterisk.MemberLocation].Paused,
            Tables = rig.Tables(),
        }.Should().BeEquivalentTo(
            new { Held = true, Loaded = 2, LatestAsked = 1, MemberPaused = false, Tables = AfterTables },
            "the load of a lost session finishes nothing: what it had read never lands on the session that replaced it");
    }

    /// <summary>Every fence a load passes, by both routes, for the automatic reload and for a host's explicit load.</summary>
    public static TheoryData<Route, string, bool> RetiredFences()
    {
        var data = new TheoryData<Route, string, bool>();
        foreach (var route in new[] { Route.Caller, Route.Loop })
        {
            foreach (var list in new[] { "Status", "BridgeList", "ConfbridgeList", "QueueStatus" })
            {
                data.Add(route, list, false);
                data.Add(route, list, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RetiredFences))]
    public async Task RetiredLoad_ShouldApplyAndAskNothingMore_WhenItsSessionIsLostAtAFence(Route route, string list,
        bool explicitLoad)
    {
        var retired = PeerRetired();
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), retired, route, laterPeers: [third]);
        using var hold = new HandlerHold();
        var loadedAtTheLoss = 1;
        if (explicitLoad)
        {
            await rig.LoseAndComeBackAsync(route);
            await rig.Run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
            await rig.SettleAsync();
            loadedAtTheLoss = 2;
        }

        // The list was read to its end on the session; what stands before applying it is the fence, and here the loss.
        rig.Run.Server.FenceWaitObserved = (name, _) =>
        {
            if (name == list)
                hold.Block();
        };
        var load = explicitLoad ? rig.Run.Server.RequestInitialStateAsync().AsTask() : Task.CompletedTask;
        if (!explicitLoad)
            await rig.LoseAndComeBackAsync(route);

        var held = await CompletesWithinBoundAsync(hold.Held);
        if (held)
        {
            retired.CloseSession();
            if (route == Route.Caller)
                await rig.CallerConnectsAsync();

            await rig.Run.ServerLog.Logged(StateLoaded, loadedAtTheLoss + 1).WaitAsync(Run.Bound);
        }

        hold.Release();
        var stopped = await Record.ExceptionAsync(() => load.WaitAsync(Run.Bound));
        await rig.SettleAsync();

        new
        {
            Held = held,
            Loaded = Lines(rig.Run, StateLoaded),
            Stopped = stopped?.GetType(),
            StatusAsked = third.Asked("Status"),
            BridgeListAsked = third.Asked("BridgeList"),
            RoomsAsked = third.Asked("ConfbridgeListRooms"),
            QueueStatusAsked = third.Asked("QueueStatus"),
            AgentsAsked = third.Asked("Agents"),
            Tables = rig.Tables(),
            GhostBridgeLive = rig.Run.Server.Bridges.GetById(GhostBridge) is { DestroyedAt: null },
        }.Should().BeEquivalentTo(
            new
            {
                Held = true,
                Loaded = loadedAtTheLoss + 1,
                Stopped = explicitLoad ? typeof(AmiNotConnectedException) : null,
                StatusAsked = 1,
                BridgeListAsked = 1,
                RoomsAsked = 1,
                QueueStatusAsked = 1,
                AgentsAsked = 1,
                Tables = AfterTables,
                GhostBridgeLive = false,
            },
            "a list read to its end on a lost session describes a session that is gone: the load stops there, applies it to "
            + "nothing and asks nothing of the session that replaced it, which has already loaded itself");
    }

    [Fact]
    public async Task RetiredLoad_ShouldAskNothingOfItsSuccessor_WhenItsQueueStatusWasRefusedAsItsSessionEnded()
    {
        var retired = PeerRetired();
        retired.QueueStatusRefusalAfterRows = "Refused";
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), retired, Route.Caller, laterPeers: [third]);
        using var hold = new HandlerHold();
        rig.Run.Server.QueueRowBuffered = hold.Block;

        // A refused list passes no fence: the request after it is the first thing that can see the session is gone.
        await rig.LoseAndComeBackAsync(Route.Caller);
        var held = await CompletesWithinBoundAsync(hold.Held);
        if (held)
        {
            retired.CloseSession();
            await rig.CallerConnectsAsync();
            await rig.Run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        }

        hold.Release();
        await rig.SettleAsync();

        new { Held = held, Loaded = Lines(rig.Run, StateLoaded), AgentsAsked = third.Asked("Agents") }.Should().BeEquivalentTo(
            new { Held = true, Loaded = 2, AgentsAsked = 1 },
            "the load of a lost session sends nothing on the session that replaced it");
    }

    [Fact]
    public async Task RetiredLoad_ShouldApplyNothing_WhenItRunsInsideAHandlerOfItsLostSession()
    {
        var retired = PeerRetired();
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), retired, Route.Loop, laterPeers: [third]);
        await rig.LoseAndComeBackAsync(Route.Loop);
        await rig.Run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        await rig.SettleAsync();
        var joinedBefore = rig.Counters.Joined;

        // A caller this load's list alone would admit. The handler holds the dispatch, so the load skips the fence.
        retired.QueueEntries = [.. retired.QueueEntries, new QueueCaller(Sentinel.Channel, Sentinel.Uid, 4)];
        using var hold = new HandlerHold();
        rig.Run.Server.QueueRowBuffered = hold.Block;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inHandler = rig.Run.Connection.Subscribe(new OnNext<ManagerEvent>(e =>
        {
            if (e is NewChannelEvent)
            {
#pragma warning disable VSTHRD002 // a handler that holds its dispatch is the case under test
                stopped.TrySetResult(Record.Exception(() => rig.Run.Server.RequestInitialStateAsync().AsTask().Wait(Run.Bound)));
#pragma warning restore VSTHRD002
            }
        }));

        await retired.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Sentinel.Uid, Sentinel.Channel, Sentinel.Uid));
        var held = await CompletesWithinBoundAsync(hold.Held);
        if (held)
        {
            retired.CloseSession();
            await rig.Run.ConnectionLog.Logged(Connected, 3).WaitAsync(Run.Bound);
        }

        hold.Release();
        var ended = await stopped.Task.WaitAsync(Run.Bound);
        await rig.Run.ServerLog.Logged(StateLoaded, 3).WaitAsync(Run.Bound);
        await rig.SettleAsync();

        new
        {
            Held = held,
            Stopped = (ended as AggregateException)?.InnerException?.GetType(),
            Joined = rig.Counters.Joined - joinedBefore,
            AgentsAsked = third.Asked("Agents"),
        }.Should().BeEquivalentTo(
            new { Held = true, Stopped = typeof(AmiNotConnectedException), Joined = 0L, AgentsAsked = 1 },
            "with no fence to pass the session is still read before the list is applied");
    }

    [Fact]
    public async Task Reload_ShouldReportItsFailureAndNoRecovery_WhenItsLoadThrows()
    {
        var failing = PeerAfter();
        var third = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), failing, Route.Caller, laterPeers: [third]);
        var thrown = 0;
        rig.Run.Server.QueueRowBuffered = () =>
        {
            if (Interlocked.Exchange(ref thrown, 1) == 0)
                throw new InvalidOperationException("the load failed for a reason of its own");
        };

        await rig.LoseAndComeBackAsync(Route.Caller);
        var reported = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(ReloadFailed, 1));
        await rig.SettleAsync();
        var afterFailure = new
        {
            Reported = reported,
            Loaded = Lines(rig.Run, StateLoaded),
            QueueCallers = rig.Tables().QueueCallers,
            rig.Run.Connection.State,
        };

        failing.CloseSession();
        await rig.CallerConnectsAsync();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        await rig.SettleAsync();

        new { AfterFailure = afterFailure, Reloaded = reloaded, StatusAsked = third.Asked("Status"), Tables = rig.Tables() }
            .Should().BeEquivalentTo(
                new
                {
                    AfterFailure = new
                    {
                        Reported = true,
                        Loaded = 1,
                        // The completed Status ended the call that left; the queue list it never finished admits nobody.
                        QueueCallers = Sorted(Stayed.Channel),
                        State = AmiConnectionState.Connected,
                    },
                    Reloaded = true,
                    StatusAsked = 1,
                    Tables = AfterTables,
                },
                "a reload that fails says so and reports no state loaded, the list it did not finish applies nothing, and the "
                + "failure spends only its own session: the next one reloads");
    }

    [Fact]
    public async Task Reload_ShouldNotStart_WhenTheServerIsDisposedAfterItsHandlerWasRead()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var run = await Run.ConnectAsync(first, second);
        // Ahead of the server's handler in the list the delivery reads once, so the disposal cannot unhook it in time.
        // Internal to the connection, and read the way SettleAsync reads its queue.
#pragma warning disable VSTHRD002 // a handler that blocks is the case under test
        Action<long> disposes = _ => run.Server.DisposeAsync().AsTask().Wait(Run.Bound);
#pragma warning restore VSTHRD002
        typeof(AmiConnection).GetEvent("CallerReconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .AddMethod!.Invoke(run.Connection, [disposes]);
        await run.StartServerAsync();

        first.CloseSession();
        AwaitState(run, s => s != AmiConnectionState.Connected);
        run.ReleaseSecondPeer();
        await run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);
        await SettleAsync(run);

        new { StatusAsked = second.Asked("Status"), ReloadsStarted = Lines(run, ReloadStarted) }.Should().BeEquivalentTo(
            new { StatusAsked = 0, ReloadsStarted = 0 },
            "the disposal came first, so the handler the delivery still calls starts nothing");
    }

    // ---- One automatic reload per session ----

    [Fact]
    public async Task Reload_ShouldRunOncePerSession_WhenAHostStartedTheServerTwice()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(first, second, Route.Caller);
        await rig.Run.StartServerAsync();

        await rig.LoseAndComeBackAsync(Route.Caller);
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 3));
        await rig.SettleAsync();

        new { Reloaded = reloaded, StatusAsked = second.Asked("Status"), ReloadsStarted = Lines(rig.Run, ReloadStarted) }
            .Should().BeEquivalentTo(
                new { Reloaded = true, StatusAsked = 1, ReloadsStarted = 1 },
                "a host that retries its start subscribes the server again, and the session still owes one automatic reload");
    }

    // ---- A host that keeps its explicit request ----

    [Theory]
    [InlineData(Route.Caller)]
    [InlineData(Route.Loop)]
    public async Task ExplicitLoad_ShouldRepresentEachCallAndVisitOnce_WhenItOverlapsTheAutomaticReload(Route route)
    {
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(PeerBefore(), second, route);
        var stayedEntry = rig.Entry(Stayed);
        using var hold = new HandlerHold();
        rig.Run.Server.QueueRowBuffered = hold.Block;

        // Whichever load reaches its queue listing first is held there, and the other runs to its end past it.
        await rig.LoseAndComeBackAsync(route);
        var explicitLoad = rig.Run.Server.RequestInitialStateAsync().AsTask();
        var overlapped = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        hold.Release();
        await explicitLoad.WaitAsync(Run.Bound);
        var bothEnded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, overlapped ? 3 : 2));
        await rig.SettleAsync();
        await rig.ObservedCallAsync(second);

        AssertOverlapLeftOneOfEach(rig, second, overlapped, bothEnded);
        rig.Entry(Stayed).Should().BeSameAs(stayedEntry, "neither load replaces the visit that survived the outage");
    }

    [Fact]
    public async Task ExplicitLoad_ShouldRepresentEachCallAndVisitOnce_WhenItBeganBeforeTheAutomaticReloadAndEndsAfterIt()
    {
        var first = PeerBefore();
        var second = PeerAfter();
        await using var rig = await Rig.StartAsync(first, second, Route.Caller);
        var stayedEntry = rig.Entry(Stayed);
        using var notice = new HandlerHold();
        using var rows = new HandlerHold();
        rig.Run.Server.ConnectionLost += _ => notice.Block();

        // The host's connect returned, and its request runs, while a slow handler still keeps the reload's notice queued.
        first.CloseSession();
        await notice.Held.WaitAsync(Run.Bound);
        rig.Run.ReleaseSecondPeer();
        await rig.CallerConnectsAsync();
        rig.Run.Server.QueueRowBuffered = rows.Block;
        var explicitLoad = rig.Run.Server.RequestInitialStateAsync().AsTask();
        var explicitHeld = await CompletesWithinBoundAsync(rows.Held);

        // So the reload reads its snapshots after the request read its own, and applies them first.
        notice.Release();
        var reloaded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 2));
        rows.Release();
        await explicitLoad.WaitAsync(Run.Bound);
        var bothEnded = await CompletesWithinBoundAsync(rig.Run.ServerLog.Logged(StateLoaded, 3));
        await rig.SettleAsync();
        await rig.ObservedCallAsync(second);

        AssertOverlapLeftOneOfEach(rig, second, explicitHeld && reloaded, bothEnded);
        rig.Entry(Stayed).Should().BeSameAs(stayedEntry, "neither load replaces the visit that survived the outage");
    }

    private static void AssertOverlapLeftOneOfEach(Rig rig, BootingAsterisk second, bool overlapped, bool bothEnded)
    {
        new
        {
            Overlapped = overlapped,
            BothEnded = bothEnded,
            StatusAsked = second.Asked("Status"),
            QueueStatusAsked = second.Asked("QueueStatus"),
            Tables = rig.Tables(),
            rig.Counters.Joined,
            rig.Counters.Left,
            Removed = rig.Removed,
            GoneEnding = rig.Ending(Gone),
        }.Should().BeEquivalentTo(
            new
            {
                Overlapped = true,
                BothEnded = true,
                StatusAsked = 2,
                QueueStatusAsked = 2,
                Tables = AfterTables,
                Joined = 1L,
                Left = 1L,
                Removed = new[] { Gone.Uid, RoomGone.Uid, Sentinel.Uid },
                GoneEnding = new Ending("reload", null, 1),
            },
            "the host's request costs one more load, and nothing else: each call and visit is held once, each join and "
            + "leave is counted once, and each ending is announced once");
    }

    // ---- The rig ----

    private static string[] Sorted(params string[] values) => [.. values.Order(StringComparer.Ordinal)];

    private static StatusChannel Status(Leg leg, string? bridgeId = null) =>
        new(leg.Uid, leg.Channel, leg.Uid, BridgeId: bridgeId);

    private static ListedRoom ListedRoomOf(params Leg[] legs) =>
        new(Room, [.. legs.Select(l => new ListedParticipant(l.Channel, l.Uid, l.Uid))]);

    private static ListedBridge RoomBridgeOf(int numChannels) =>
        new(RoomBridge, numChannels, Type: "base", Technology: "softmix", Creator: "ConfBridge", Name: Room);

    /// <summary>Asterisk before the outage: two calls waiting in the queue, two participants in the room.</summary>
    private static BootingAsterisk PeerBefore() => new()
    {
        BootedAtLogin = true,
        StatusChannels = [Status(Stayed), Status(Gone), Status(RoomStayed, RoomBridge), Status(RoomGone, RoomBridge)],
        QueueEntries = [new QueueCaller(Stayed.Channel, Stayed.Uid, 1), new QueueCaller(Gone.Channel, Gone.Uid, 2)],
        Bridges = [RoomBridgeOf(2)],
        Rooms = [ListedRoomOf(RoomStayed, RoomGone)],
    };

    /// <summary>Asterisk after it: one of each ended during the outage and one of each started.</summary>
    private static BootingAsterisk PeerAfter() => new()
    {
        BootedAtLogin = true,
        StatusChannels = [Status(Stayed), Status(Started), Status(RoomStayed, RoomBridge), Status(RoomStarted, RoomBridge)],
        QueueEntries = [new QueueCaller(Stayed.Channel, Stayed.Uid, 1), new QueueCaller(Started.Channel, Started.Uid, 2)],
        Bridges = [RoomBridgeOf(2)],
        Rooms = [ListedRoomOf(RoomStayed, RoomStarted)],
    };

    /// <summary>A session that is lost in its turn: it lists what the session after it no longer does.</summary>
    private static BootingAsterisk PeerRetired() => new()
    {
        BootedAtLogin = true,
        StatusChannels =
        [
            Status(Stayed), Status(Started), Status(Ghost), Status(RoomStayed, RoomBridge), Status(RoomStarted, RoomBridge),
            Status(RoomGhost, RoomBridge),
        ],
        QueueEntries =
        [
            new QueueCaller(Stayed.Channel, Stayed.Uid, 1), new QueueCaller(Started.Channel, Started.Uid, 2),
            new QueueCaller(Ghost.Channel, Ghost.Uid, 3),
        ],
        Bridges = [RoomBridgeOf(3), new ListedBridge(GhostBridge, 0)],
        Rooms = [ListedRoomOf(RoomStayed, RoomStarted, RoomGhost)],
    };

    private static int Lines(Run run, string fragment) =>
        run.ServerLog.Entries.Count(e => e.Line.Contains(fragment, StringComparison.Ordinal));

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for the state to match, read directly: a test holding the notification queue never sees it on
    /// <c>StateChanged</c>. <paramref name="when"/> false skips the wait of a step the run has already failed to reach.
    /// </summary>
    private static void AwaitState(Run run, Func<AmiConnectionState, bool> match, bool when = true)
    {
        if (when)
        {
            SpinWait.SpinUntil(() => match(run.Connection.State), Run.Bound) // fence-allow: GUARD-TIMEOUT — failure bound on an observed state, never the winning arm of a green run
                .Should().BeTrue($"the connection reaches the awaited state; it reads {run.Connection.State}");
        }
    }

    /// <summary>See the class remarks: after it, nothing already started is still to be counted.</summary>
    private static async Task SettleAsync(Run run)
    {
        // Internal to the connection and not part of what Live binds to, so it is read the way these tests read its pump.
        var pending = (Task)typeof(AmiConnection)
            .GetProperty("PendingNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(run.Connection)!;
        await pending.WaitAsync(Run.Bound);
        SpinWait.SpinUntil(() => !run.Server.IsLoadInFlight, Run.Bound) // fence-allow: GUARD-TIMEOUT — failure bound on the end of a load, never the winning arm of a green run
            .Should().BeTrue("every load ends");
        if (run.Connection.State == AmiConnectionState.Connected)
            await run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);
    }

    private sealed record Leg(string Uid, string Channel);

    private sealed record Tables(string[] Channels, string[] QueueCallers, string[] RoomUsers);

    private sealed record Ending(string? CauseMetadata, HangupCause? HangupCause, int CallEndedRaised);

    /// <summary>
    /// A started server over <see cref="Run"/>, with a session manager attached before its start, the channels it removes
    /// and the counters from the moment the start has returned.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CallSessionManager _manager = new(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore());
        private readonly ConcurrentQueue<string> _removed = new();
        private readonly ConcurrentDictionary<string, int> _callEnded = new(StringComparer.Ordinal);
        private readonly IDisposable _subscription;

        private Rig(Run run)
        {
            Run = run;
            _subscription = _manager.Events.Subscribe(new OnNext<SessionDomainEvent>(e =>
            {
                if (e is CallEndedEvent ended && _manager.GetById(ended.SessionId) is { LinkedId: { } linkedId })
                    _callEnded.AddOrUpdate(linkedId, 1, static (_, count) => count + 1);
            }));
            _manager.AttachToServer(run.Server, "srv-1");
            run.Server.Channels.ChannelRemoved += c => _removed.Enqueue(c.UniqueId);
        }

        public Run Run { get; }

        /// <summary>The queue's and the bridges' counters, since the start returned.</summary>
        public LiveCounters Counters { get; private set; } = null!;

        /// <summary>The channels removed, in order, as <c>ChannelRemoved</c> announced them.</summary>
        public string[] Removed => [.. _removed];

        public static async Task<Rig> StartAsync(BootingAsterisk first, BootingAsterisk second, Route route,
            int maxReconnectAttempts = 0, IReadOnlyList<BootingAsterisk>? laterPeers = null)
        {
            var run = await Run.ConnectAsync(first, second, autoReconnect: route == Route.Loop,
                maxReconnectAttempts: maxReconnectAttempts, laterPeers: laterPeers);
            var rig = new Rig(run);
            try
            {
                await run.StartServerAsync();
                await run.ServerLog.Logged(StateLoaded, 1).WaitAsync(Run.Bound);
                rig.Counters = new LiveCounters();
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Ends the first session and lets the second peer serve what follows: over <see cref="Route.Caller"/> the caller's
        /// connect, and over <see cref="Route.Loop"/> the loop's; either way the new session has logged in on return.
        /// </summary>
        public async Task LoseAndComeBackAsync(Route route)
        {
            Run.FirstPeer.CloseSession();
            Run.ReleaseSecondPeer();
            if (route == Route.Caller)
                await CallerConnectsAsync();
            else
                await Run.ConnectionLog.Logged(Connected, 2).WaitAsync(Run.Bound);
        }

        /// <summary>The caller's connect, once the last session has ended, which must succeed.</summary>
        public async Task CallerConnectsAsync()
        {
            AwaitState(Run, s => s != AmiConnectionState.Connected);
            await Run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);
        }

        /// <summary><see cref="CallerConnectsAsync"/> when <paramref name="when"/>: a step the run may have failed to reach.</summary>
        public Task CallerConnectsAfterAsync(bool when) => when ? CallerConnectsAsync() : Task.CompletedTask;

        /// <summary>The caller's connect, whose outcome is under test.</summary>
        public Task<Exception?> CallerConnectsCapturingAsync() =>
            Record.ExceptionAsync(() => Run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound));

        /// <summary>One round trip on the live session: every action sent before it has reached the peer.</summary>
        public async Task PingAsync() =>
            _ = await Run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);

        /// <inheritdoc cref="CallerReconnectReloadTests.SettleAsync(Run)"/>
        public Task SettleAsync() => CallerReconnectReloadTests.SettleAsync(Run);

        /// <summary>
        /// The peer opens and hangs up one more call and the test waits for its removal: the announcements queued before it
        /// have been delivered.
        /// </summary>
        public async Task ObservedCallAsync(BootingAsterisk peer)
        {
            var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Run.Server.Channels.ChannelRemoved += c =>
            {
                if (c.UniqueId == Sentinel.Uid)
                    removed.TrySetResult();
            };
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Sentinel.Uid, Sentinel.Channel, Sentinel.Uid));
            await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Sentinel.Uid, Sentinel.Channel, Sentinel.Uid));
            await removed.Task.WaitAsync(Run.Bound);
        }

        public Tables Tables()
        {
            var server = Run.Server;
            return new Tables(
                Sorted([.. server.Channels.ActiveChannels.Select(c => c.UniqueId)]),
                Sorted([.. server.Queues.GetByName(BootingAsterisk.QueueName)?.Entries.Keys ?? []]),
                Sorted([.. server.MeetMe.GetRoom(Room)?.Users.Keys ?? []]));
        }

        public Live.Queues.AsteriskQueueEntry? Entry(Leg leg) =>
            Run.Server.Queues.GetByName(BootingAsterisk.QueueName)?.Entries.GetValueOrDefault(leg.Channel);

        public Ending Ending(Leg leg)
        {
            var session = _manager.GetByLinkedId(leg.Uid);
            return new Ending(session?.Metadata.GetValueOrDefault("cause"), session?.HangupCause,
                _callEnded.GetValueOrDefault(leg.Uid));
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            Counters?.Dispose();
            await _manager.DisposeAsync();
            await Run.DisposeAsync();
        }
    }

    /// <summary>A subscriber that runs <c>onNext</c> on each notification.</summary>
    private sealed class OnNext<T>(Action<T> onNext) : IObserver<T>
    {
        void IObserver<T>.OnNext(T value) => onNext(value);

        public void OnError(Exception error)
        {
            // Neither stream faults in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when its source is disposed; nothing to do.
        }
    }

    /// <summary>A store that saves nothing; the tests read the manager.</summary>
    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}
