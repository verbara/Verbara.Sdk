using System.Collections.Concurrent;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A load of live state over a session that logged in while Asterisk was still loading its modules, and a load
/// whose session ends before the load does.
/// </summary>
/// <remarks>
/// <para>
/// Asterisk accepts the AMI login before app_queue and app_agent_pool have registered <c>QueueStatus</c> and
/// <c>Agents</c>, and refuses each as <c>Invalid/unknown command</c> until they have; it reports the end of its start
/// with <c>FullyBooted</c>, and only to AMI users with <c>system</c> in their read permissions. The window, as
/// measured, is described on <see cref="BootingAsterisk"/>, the peer that plays it. Asterisk can also close a session
/// it has just opened: an action that arrives while its module is registering it ends the session with
/// <c>Permission denied</c>. Over 60 cold starts, 20 each on Asterisk 20.20.1, 22.9.0 and 23.4.1, it closed the fresh
/// session right after the login twice, both on 22.9.0, and the start failed although the connection was about to
/// reconnect and reload.
/// </para>
/// <para>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/>
/// through <see cref="Run"/>. The peer boots when the test says so, the load's waits run on <see cref="Run.Clock"/>,
/// and every await is bounded by <see cref="Run.Bound"/> and ends on its signal. A test that moves the clock first
/// reads the load's timer from <see cref="FakeTimeProvider.TimersCreated"/>, so it knows the load is parked on it.
/// </para>
/// </remarks>
public sealed class VerbaraServerBootWindowTests
{
    private const string StateLoaded = "[LIVE] State loaded";

    private const string ReconnectReloadFailed = "[LIVE] Reconnect reload failed";

    private const string InitialLoadInterrupted = "[LIVE] Initial state load interrupted";

    private const string ReloadInterrupted = "[LIVE] Reconnect reload interrupted";

    /// <summary>What the interruption's warning says when the session ended while the load waited for <c>FullyBooted</c>.</summary>
    private const string EndedDuringTheWait = "while the live state load waited for FullyBooted";

    /// <summary>How long a load waits between two asks when the user never receives <c>FullyBooted</c>.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long after its first refusal a load stops asking.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most waits a test drives a load through: 30 s of manual time at <see cref="Interval"/>, past both the
    /// budget and twice the budget, so a load that waits too long reports how long instead of the test's bound.
    /// </summary>
    private const int MaxWaitsDriven = 150;

    [Fact]
    public async Task StartAsync_ShouldLoadTheQueueItsMemberAndTheAgentAskingEachOnce_WhenAsteriskHasBootedAndTheUserReceivesFullyBooted()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SendsFullyBooted = true };

        await using var run = await Run.StartAsync(peer);
        var loaded = await CompletesWithinBoundAsync(run.ServerLog.Logged(StateLoaded));

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            loaded.Should().BeTrue("a start that returned has logged its load");
            run.Server.Queues.QueueCount.Should().Be(1, "Asterisk reports one queue");
            queue.Should().NotBeNull("the queue Asterisk reports is loaded");
            queue!.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue!.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.AgentCount.Should().Be(1, "Asterisk reports one agent");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            peer.Asked("Status").Should().Be(1, "the channels are asked once");
            peer.Asked("QueueStatus").Should().Be(1, "the queues are asked once");
            peer.Asked("Agents").Should().Be(1, "the agents are asked once");
            run.Clock.TimersCreated.TryRead(out _).Should().BeFalse("a load that was answered waits for nothing");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    // ── A refusal from a starting Asterisk is not an empty answer ───────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ShouldLoadTheQueueAndTheAgent_WhenTheSessionLoggedInBeforeTheirModulesLoaded()
    {
        // A user with `system`: Asterisk reports FullyBooted once it has started, and the test lets it finish starting
        // as soon as the first refusal has been answered.
        var peer = new BootingAsterisk { SendsFullyBooted = true };
        await using var run = await Run.ConnectAsync(peer);

        var start = run.StartServerAsync();
        var refused = await CompletesWithinBoundAsync(peer.FirstRefusalAnswered);
        var reported = await peer.BootAsync().WaitAsync(Run.Bound);
        var outcome = await Record.ExceptionAsync(() => start);

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            refused.Should().BeTrue("the queues were asked before Asterisk had registered QueueStatus");
            reported.Should().BeTrue("Asterisk reported FullyBooted on the session the load was using");
            outcome.Should().BeNull("a load that was refused by a starting Asterisk completes once it has started");
            queue.Should().NotBeNull(
                "a refusal from an Asterisk that is still starting is not an empty table: the queue is asked again");
            queue!.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue!.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            peer.Asked("QueueStatus").Should().Be(2, "once refused, then once more after Asterisk reported FullyBooted");
            peer.Asked("Agents").Should().Be(1, "the agents are asked once Asterisk has started, and answered");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task Reload_ShouldLoadTheQueueAndTheAgent_WhenTheReconnectLandsInABootWindow()
    {
        // The first session saw FullyBooted; the second logs in while Asterisk is starting again. A report from the
        // first session must not stand for the second one.
        var first = new BootingAsterisk { BootedAtLogin = true, SendsFullyBooted = true };
        var second = new BootingAsterisk { SendsFullyBooted = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var loadedAtStart = run.Server.Queues.GetByName(BootingAsterisk.QueueName) is not null;

        run.ReleaseSecondPeer();
        first.CloseSession();
        var reconnected = await CompletesWithinBoundAsync(run.Reconnected);
        var refused = await CompletesWithinBoundAsync(second.FirstRefusalAnswered);
        var reported = await second.BootAsync().WaitAsync(Run.Bound);
        var reloaded = await CompletesWithinBoundAsync(run.ServerLog.Logged(StateLoaded, times: 2));

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            loadedAtStart.Should().BeTrue("the start ran on an Asterisk that had booted");
            reconnected.Should().BeTrue("the connection reconnected to the second session");
            refused.Should().BeTrue("the reload asked the second session before Asterisk had registered QueueStatus");
            reported.Should().BeTrue("Asterisk reported FullyBooted on the second session");
            reloaded.Should().BeTrue("the reload completed and logged its load");
            queue.Should().NotBeNull(
                "the first session's FullyBooted does not count for the second: the reload asks again");
            queue!.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue!.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is reloaded");
            second.Asked("QueueStatus").Should().Be(2, "once refused, then once more after the second session's report");
            Lines(run.ServerLog, ReconnectReloadFailed).Should().Be(0, "the reload completed");
            first.Fault.Should().BeNull("the first peer served its session without failing");
            second.Fault.Should().BeNull("the second peer served its session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldAskAgainAtTheInterval_WhenTheUserNeverReceivesFullyBootedDuringTheBoot()
    {
        // A user without `system`: no FullyBooted ever arrives, so the load asks again at the interval. The test
        // moves the clock one interval per step, and Asterisk finishes starting after the second step.
        var peer = new BootingAsterisk { SendsFullyBooted = false };
        await using var run = await Run.ConnectAsync(peer);

        var start = run.StartServerAsync();
        const int stepsRefused = 2;
        var waits = new List<TimeSpan>();
        var asksWhenParked = new List<int>();
        for (var step = 0; step <= stepsRefused; step++)
        {
            if (await NextWaitOrEndAsync(run, start) is not { } timer)
                break;

            waits.Add(timer.DueTime);
            asksWhenParked.Add(peer.Asked("QueueStatus"));
            if (step == stepsRefused)
                await peer.BootAsync().WaitAsync(Run.Bound);

            run.Clock.Advance(Interval);
        }

        var outcome = await Record.ExceptionAsync(() => start);

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            waits.Should().Equal([Interval, Interval, Interval],
                "a refused load waits the interval before each new ask while no FullyBooted arrives");
            asksWhenParked.Should().Equal([1, 2, 3], "the queues are asked exactly once per step");
            outcome.Should().BeNull("the load completes once Asterisk answers");
            peer.Asked("QueueStatus").Should().Be(4, "once at first, then once per step, the last one answered");
            queue.Should().NotBeNull("the queue is loaded once Asterisk has started");
            queue!.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue!.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            peer.Asked("Agents").Should().Be(1, "the agents are asked once Asterisk has started, and answered");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldWaitTheBudgetOnce_WhenTheUserNeverReceivesFullyBootedAndAppQueueIsAbsent()
    {
        // The accepted residual: a user without `system` cannot tell a module still loading from one that is not
        // loaded at all, so the load asks at the interval until the budget runs out, and then loads without it.
        var peer = new BootingAsterisk { BootedAtLogin = true, SendsFullyBooted = false, HasAppQueue = false };
        await using var run = await Run.ConnectAsync(peer);

        var began = run.Clock.GetTimestamp();
        var start = run.StartServerAsync();
        var waits = await DriveTheLoadAsync(run, start);
        var waited = run.Clock.GetElapsedTime(began);
        var outcome = await Record.ExceptionAsync(() => start);

        using (new AssertionScope())
        {
            outcome.Should().BeNull("a load that ran out of budget completes without the refused state");
            waited.Should().Be(Budget, "the load stops asking 10 s after its first refusal");
            waits.Should().NotBeEmpty("the load waited before it gave up")
                .And.OnlyContain(wait => wait == Interval, "every wait is the interval");
            peer.Asked("QueueStatus").Should().Be(waits.Count + 1, "once at first, then once after each wait");
            run.Server.Queues.QueueCount.Should().Be(0, "app_queue is not loaded, so there are no queues");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            peer.Asked("Agents").Should().Be(1, "app_agent_pool is loaded, so the agents are answered at once");
            Warnings(run).Should().ContainSingle("the load warns once, when it gives up on the queues")
                .Which.Should().Contain("QueueStatus", "the warning names the action Asterisk never registered");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldWaitTheBudgetOnce_WhenTheUserNeverReceivesFullyBootedAndBothModulesAreAbsent()
    {
        // The budget belongs to the load, not to each request: with both modules absent the load waits it once.
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true, SendsFullyBooted = false, HasAppQueue = false, HasAgentPool = false,
        };
        await using var run = await Run.ConnectAsync(peer);

        var began = run.Clock.GetTimestamp();
        var start = run.StartServerAsync();
        var waits = await DriveTheLoadAsync(run, start);
        var waited = run.Clock.GetElapsedTime(began);
        var outcome = await Record.ExceptionAsync(() => start);

        using (new AssertionScope())
        {
            outcome.Should().BeNull("a load that ran out of budget completes without the refused state");
            waited.Should().Be(Budget, "the whole load waits the 10 s budget once, not once per refused request");
            waits.Should().NotBeEmpty("the load waited before it gave up")
                .And.OnlyContain(wait => wait == Interval, "every wait is the interval");
            peer.Asked("QueueStatus").Should().Be(waits.Count + 1, "once at first, then once after each wait");
            peer.Asked("Agents").Should().Be(1, "the budget was spent when the agents were refused, so they are not asked again");
            run.Server.Queues.QueueCount.Should().Be(0, "app_queue is not loaded, so there are no queues");
            run.Server.Agents.AgentCount.Should().Be(0, "app_agent_pool is not loaded, so there are no agents");
            Warnings(run).Should().SatisfyRespectively(
                queues => queues.Should().Contain("QueueStatus", "the first warning names the queues' action"),
                agents => agents.Should().Contain("Agents", "the second warning names the agents' action"));
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    // ── A load cut short by the end of its session ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ShouldReturnAndLeaveTheLoadToTheReconnect_WhenAsteriskClosesTheSessionRightAfterTheLogin()
    {
        // Asterisk answers the connect and closes the session before the start has asked anything. The start begins
        // once the connection is reconnecting. The reconnect's first backoff is the option's default, 1 s, which is
        // the connection's own delay: the test waits on the reconnect's log line and on Reconnected, never on a clock.
        var first = new BootingAsterisk { BootedAtLogin = true, Close = PeerClose.AfterConnect };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true,
            reconnectInitialDelay: TimeSpan.FromSeconds(1));

        var reconnecting = await CompletesWithinBoundAsync(run.ConnectionLog.Logged("[AMI] Reconnecting"));
        var stateAtStart = run.Connection.State;
        var outcome = await Record.ExceptionAsync(run.StartServerAsync);
        run.ReleaseSecondPeer();
        var reconnected = await CompletesWithinBoundAsync(run.Reconnected);
        var reloaded = await CompletesWithinBoundAsync(run.ServerLog.Logged(StateLoaded));

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            outcome.Should().BeNull(
                "the connection is reconnecting, and the reload that follows the reconnect loads the state");
            reconnecting.Should().BeTrue("the connection started reconnecting once Asterisk closed the session");
            stateAtStart.Should().Be(AmiConnectionState.Reconnecting, "the start began on a connection already reconnecting");
            reconnected.Should().BeTrue("the connection reconnected to the second session");
            reloaded.Should().BeTrue("the reload after the reconnect completed and logged its load");
            queue.Should().NotBeNull("the reload loads the queue");
            queue!.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue!.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the reload loads the agent");
            Lines(run.ServerLog, ReconnectReloadFailed).Should().Be(0, "the reload completed");
            Warnings(run).Should().ContainSingle("the start's interrupted load warns once, and the reload after it completes")
                .Which.Should().Contain(InitialLoadInterrupted, "the warning says that the start's load was interrupted");
            first.Fault.Should().BeNull("the first peer served its session without failing");
            second.Fault.Should().BeNull("the second peer served its session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldThrowNotConnected_WhenTheSessionEndsDuringTheLastRequestAndAutoReconnectIsOff()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, Close = PeerClose.WhenAsked, CloseWhenAsked = "Agents" };
        await using var run = await Run.ConnectAsync(peer);

        var outcome = await Record.ExceptionAsync(run.StartServerAsync);

        using (new AssertionScope())
        {
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "the connection will not come back, so nothing will ever reload what the load did not finish");
            Lines(run.ServerLog, StateLoaded).Should().Be(0, "a load cut short by its session is not reported as loaded");
            peer.Asked("Status").Should().Be(1, "the channels were answered before the session ended");
            peer.Asked("QueueStatus").Should().Be(1, "the queues were answered before the session ended");
            peer.Asked("Agents").Should().Be(1, "the session ended while the agents, the last request, were pending");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldEndNoCall_WhenTheSessionEndsWhileTheChannelSnapshotIsRead()
    {
        // Two calls, one channel each, held from the start. The next snapshot lists the first channel and the session
        // ends before StatusComplete: the second channel's absence from an unfinished snapshot proves nothing.
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels =
            [
                new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
                new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.2"),
            ],
        };
        await using var run = await Run.StartAsync(peer);
        var heldAtStart = run.Server.Channels.ChannelCount;
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.Close = PeerClose.DuringStatus;
        peer.StatusChannelsBeforeClose = 1;
        var outcome = await Record.ExceptionAsync(
            () => run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            removed.Should().BeEmpty("a snapshot the session ended before it completed ends no call");
            run.Server.Channels.ChannelCount.Should().Be(2, "both channels are still held");
            run.Server.Channels.GetByUniqueId("1700000000.1").Should().NotBeNull("the listed channel is still held");
            run.Server.Channels.GetByUniqueId("1700000000.2").Should().NotBeNull(
                "the channel the unfinished snapshot never reached is still held");
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "a direct call to the load throws when its session ends, and returns no partial state");
            heldAtStart.Should().Be(2, "the start loaded both channels");
            peer.Asked("Status").Should().Be(2, "the start's snapshot and the reload's");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldThrowNotConnected_WhenTheSessionEndsDuringAnEarlierRequestAndAutoReconnectIsOff()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true, Close = PeerClose.WhenAsked, CloseWhenAsked = "QueueStatus",
        };
        await using var run = await Run.ConnectAsync(peer);

        var outcome = await Record.ExceptionAsync(run.StartServerAsync);
        var sentAfterClose = await peer.ActionsSentAfterCloseAsync(Run.Bound);

        using (new AssertionScope())
        {
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "the connection will not come back, so nothing will ever reload what the load did not finish");
            Lines(run.ServerLog, StateLoaded).Should().Be(0, "a load cut short by its session is not reported as loaded");
            peer.Asked("QueueStatus").Should().Be(1, "the session ended while the queues were pending");
            sentAfterClose.Should().BeEmpty("nothing more is sent on a session that has ended");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldReturn_WhenTheSessionEndsWhileTheLoadWaitsForFullyBooted()
    {
        // Asterisk refuses the queues while it starts, then closes the session before it reports FullyBooted. The
        // second session is served only once the start has returned or thrown, so a load that asks again on the
        // ended session meets a connection that is reconnecting.
        var first = new BootingAsterisk { SendsFullyBooted = true, Close = PeerClose.AfterFirstRefusal };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);

        var outcome = await Record.ExceptionAsync(run.StartServerAsync);
        run.ReleaseSecondPeer();
        var reconnected = await CompletesWithinBoundAsync(run.Reconnected);
        var reloaded = await CompletesWithinBoundAsync(run.ServerLog.Logged(StateLoaded));

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            outcome.Should().BeNull(
                "the connection is reconnecting, and the reload that follows the reconnect loads the state");
            first.FirstRefusalAnswered.IsCompleted.Should().BeTrue("the queues were refused before the session ended");
            reconnected.Should().BeTrue("the connection reconnected to the second session");
            reloaded.Should().BeTrue("the reload after the reconnect completed and logged its load");
            queue.Should().NotBeNull("the reload loads the queue");
            queue!.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue!.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the reload loads the agent");
            Lines(run.ServerLog, ReconnectReloadFailed).Should().Be(0, "the reload completed");
            Warnings(run).Should().ContainSingle("the start's interrupted load warns once, and the reload after it completes")
                .Which.Should().Contain(InitialLoadInterrupted, "the warning says that the start's load was interrupted")
                .And.Contain(EndedDuringTheWait, "the load stopped at its wait, and asked nothing on the ended session");
            first.Fault.Should().BeNull("the first peer served its session without failing");
            second.Fault.Should().BeNull("the second peer served its session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldAskNothingMore_WhenTheSessionEndsDuringTheWait()
    {
        // The same closure, observed from the closed session. An ask on it either reaches the peer after the close,
        // or is refused by the connection with AmiNotConnectedException because the session has ended: both are an
        // ask the load should not have made. The reconnect's socket is never served here.
        var peer = new BootingAsterisk { SendsFullyBooted = true, Close = PeerClose.AfterFirstRefusal };
        await using var run = await Run.ConnectAsync(peer, autoReconnect: true);

        var outcome = await Record.ExceptionAsync(run.StartServerAsync);
        var sentAfterClose = await peer.ActionsSentAfterCloseAsync(Run.Bound);

        using (new AssertionScope())
        {
            sentAfterClose.Should().BeEmpty("the closed session reads no action after its close");
            outcome.Should().BeNull(
                "the load asks nothing more once its session has ended, and the connection is reconnecting");
            peer.Asked("QueueStatus").Should().Be(1, "the queues were asked once, and refused, before the close");
            peer.Asked("Agents").Should().Be(0, "the load stopped before its next request");
            Warnings(run).Should().ContainSingle("the start's interrupted load warns once")
                .Which.Should().Contain(InitialLoadInterrupted, "the warning says that the start's load was interrupted")
                .And.Contain(EndedDuringTheWait,
                    "the load stopped at its wait: an ask on the ended session would be refused, and say so");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldThrowNotConnected_WhenTheSessionEndsDuringTheWaitAndAutoReconnectIsOff()
    {
        // The same closure with nothing left to reload it: the start fails rather than run on an empty table.
        var peer = new BootingAsterisk { SendsFullyBooted = true, Close = PeerClose.AfterFirstRefusal };
        await using var run = await Run.ConnectAsync(peer);

        var outcome = await Record.ExceptionAsync(run.StartServerAsync);

        using (new AssertionScope())
        {
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "the connection will not come back, so nothing will ever reload what the load did not finish")
                .Which.Message.Should().Contain(EndedDuringTheWait,
                    "the load stopped at its wait, and asked nothing on the ended session");
            Warnings(run).Should().BeEmpty("a start that throws is not an interrupted load");
            Lines(run.ServerLog, StateLoaded).Should().Be(0, "a load cut short by its session is not reported as loaded");
            peer.Asked("QueueStatus").Should().Be(1, "the queues were asked once, and refused, before the close");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task Reload_ShouldLogAnInterruptionAndAskNothingMore_WhenTheReconnectedSessionEndsDuringTheReload()
    {
        // The start loads on a booted session. After the reconnect, Asterisk ends the second session while the reload's
        // queues are pending. The connection reconnects again, and that attempt is never served: the next reload is
        // the next reconnect's.
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, Close = PeerClose.WhenAsked, CloseWhenAsked = "QueueStatus" };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);

        run.ReleaseSecondPeer();
        first.CloseSession();
        var reconnected = await CompletesWithinBoundAsync(run.Reconnected);
        var logged = await CompletesWithinBoundAsync(
            Task.WhenAny(run.ServerLog.Logged(ReloadInterrupted), run.ServerLog.Logged(ReconnectReloadFailed)));
        var sentAfterClose = await second.ActionsSentAfterCloseAsync(Run.Bound);

        using (new AssertionScope())
        {
            reconnected.Should().BeTrue("the connection reconnected to the second session");
            logged.Should().BeTrue("the reload ended, and said how");
            Lines(run.ServerLog, ReconnectReloadFailed).Should().Be(0,
                "a reload whose session ended is interrupted, not failed: the next reconnect reloads");
            Warnings(run).Should().ContainSingle("the interrupted reload warns once")
                .Which.Should().Contain(ReloadInterrupted, "the warning says that the reload was interrupted")
                .And.Contain("QueueStatus", "the warning names the request the session ended during");
            Lines(run.ServerLog, StateLoaded).Should().Be(1, "only the start's load completed");
            second.Asked("QueueStatus").Should().Be(1, "the reload asked the queues once, and the session ended");
            second.Asked("Agents").Should().Be(0, "the reload stopped before its next request");
            sentAfterClose.Should().BeEmpty("nothing more is sent on a session that has ended");
            first.Fault.Should().BeNull("the first peer served its session without failing");
            second.Fault.Should().BeNull("the second peer served its session without failing");
        }
    }

    // ── What must not move: a load with nothing to repair asks once and waits for nothing ───────────────────────────────

    /// <summary>
    /// Asterisk has started and reports <c>FullyBooted</c> once the connect is done, before the load asks anything.
    /// Without app_queue it refuses <c>QueueStatus</c> for good, and a refusal that comes after the report is the true
    /// answer: there are no queues.
    /// </summary>
    [Fact]
    public async Task StartAsync_ShouldAskOnce_WhenAppQueueIsNotLoadedAtAll()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SendsFullyBooted = true, HasAppQueue = false };
        await using var run = await Run.ConnectAsync(peer);

        var start = run.StartServerAsync();
        var waits = await DriveTheLoadAsync(run, start);
        var outcome = await Record.ExceptionAsync(() => start);

        using (new AssertionScope())
        {
            outcome.Should().BeNull("a load whose answers are all final completes");
            waits.Should().BeEmpty("Asterisk reported FullyBooted before the queues were asked, so their refusal is final");
            peer.Asked("QueueStatus").Should().Be(1, "a refusal after the report is not asked again");
            run.Server.Queues.QueueCount.Should().Be(0, "app_queue is not loaded, so there are no queues");
            peer.Asked("Agents").Should().Be(1, "the agents are asked once, and answered");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            Lines(run.ServerLog, StateLoaded).Should().Be(1, "the load completed once");
            Warnings(run).Should().BeEmpty("a module that is not loaded is not a failure to warn about");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    /// <summary>
    /// The same PBX, where Asterisk sends <c>FullyBooted</c> while the connection is still reading its connect's own
    /// responses, which is the order an Asterisk that has already started uses: right after the login's response and
    /// before the version probe's, or even before the login's response. The load that follows must know about it.
    /// </summary>
    [Theory]
    [InlineData(FullyBootedPlacement.AfterLoginResponse)]
    [InlineData(FullyBootedPlacement.BeforeLoginResponse)]
    public async Task StartAsync_ShouldAskOnce_WhenFullyBootedArrivedDuringTheLogin(FullyBootedPlacement placement)
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true, SendsFullyBooted = true, FullyBootedAt = placement, HasAppQueue = false,
        };
        await using var run = await Run.ConnectAsync(peer);

        var start = run.StartServerAsync();
        var waits = await DriveTheLoadAsync(run, start);
        var outcome = await Record.ExceptionAsync(() => start);

        using (new AssertionScope())
        {
            outcome.Should().BeNull("a load whose answers are all final completes");
            waits.Should().BeEmpty(
                "Asterisk reported FullyBooted during the login, before the queues were asked, so their refusal is final");
            peer.Asked("QueueStatus").Should().Be(1, "a refusal after the report is not asked again");
            run.Server.Queues.QueueCount.Should().Be(0, "app_queue is not loaded, so there are no queues");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            Lines(run.ServerLog, StateLoaded).Should().Be(1, "the load completed once");
            Warnings(run).Should().BeEmpty("a module that is not loaded is not a failure to warn about");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    /// <summary>
    /// A user without <c>system</c> never receives <c>FullyBooted</c>, but on an Asterisk that has started every request
    /// is answered at once. Nothing is refused, so nothing is waited for.
    /// </summary>
    [Fact]
    public async Task StartAsync_ShouldNotWait_WhenTheUserNeverReceivesFullyBootedAndAsteriskHasBooted()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SendsFullyBooted = false };
        await using var run = await Run.ConnectAsync(peer);

        var start = run.StartServerAsync();
        var waits = await DriveTheLoadAsync(run, start);
        var outcome = await Record.ExceptionAsync(() => start);

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            outcome.Should().BeNull("every request is answered");
            waits.Should().BeEmpty("an answered request is not followed by a wait");
            peer.Asked("Status").Should().Be(1, "the channels are asked once");
            peer.Asked("QueueStatus").Should().Be(1, "the queues are asked once");
            peer.Asked("Agents").Should().Be(1, "the agents are asked once");
            queue.Should().NotBeNull("the queue Asterisk reports is loaded");
            queue!.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue!.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            Warnings(run).Should().BeEmpty("nothing went wrong");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    /// <summary>
    /// Asterisk refuses the queues and the agents with <c>Permission denied</c>, and keeps the session. Only an
    /// <c>Invalid/unknown command</c> means a module may still be loading; any other refusal is final, as it is today.
    /// The user receives no <c>FullyBooted</c>, so the refusal's text is the only thing that decides.
    /// </summary>
    [Fact]
    public async Task StartAsync_ShouldNotAskAgain_WhenTheRefusalIsNotAnUnknownCommand()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true, SendsFullyBooted = false, HasAppQueue = false, HasAgentPool = false,
            RefusalMessage = BootingAsterisk.PermissionDenied,
        };
        await using var run = await Run.ConnectAsync(peer);

        var start = run.StartServerAsync();
        var waits = await DriveTheLoadAsync(run, start);
        var outcome = await Record.ExceptionAsync(() => start);

        using (new AssertionScope())
        {
            outcome.Should().BeNull("a refusal other than an unknown command ends its request, as it does today");
            waits.Should().BeEmpty("only an unknown command is waited for");
            peer.Asked("QueueStatus").Should().Be(1, "a Permission denied is not asked again");
            peer.Asked("Agents").Should().Be(1, "a Permission denied is not asked again");
            run.Server.Queues.QueueCount.Should().Be(0, "the refused request loaded no queue");
            run.Server.Agents.AgentCount.Should().Be(0, "the refused request loaded no agent");
            Lines(run.ServerLog, StateLoaded).Should().Be(1, "the load completed once");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    /// <summary>
    /// A start before the connection was ever established throws what it throws today, from the load's first request:
    /// on a connection never connected, and on one whose caller's first connect is still running. Neither has a
    /// reconnect that would reload afterwards.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_ShouldThrowNotConnected_WhenTheConnectionWasNeverConnected(bool firstConnectRunning)
    {
        var sockets = new PipedSocketFactory();
        var serverLog = new SignalingLogger<VerbaraServer>();
        await using var connection = new AmiConnection(
            Microsoft.Extensions.Options.Options.Create(new AmiConnectionOptions
            {
                Hostname = "localhost",
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = true,
                // A limit, never a wait: the first connect the test holds must not give up while it holds it.
                ConnectionTimeout = TimeSpan.FromMinutes(1),
            }),
            sockets,
            new SignalingLogger<AmiConnection>());
        await using var server = new VerbaraServer(connection, serverLog);

        Task? firstConnect = null;
        if (firstConnectRunning)
        {
            // The connect writes Connecting before it creates its socket, and then waits for a banner nobody sends.
            firstConnect = connection.ConnectAsync().AsTask();
            await sockets.NextAsync(CancellationToken.None).AsTask().WaitAsync(Run.Bound);
        }

        var stateAtStart = connection.State;
        var outcome = await Record.ExceptionAsync(() => server.StartAsync().WaitAsync(Run.Bound));
        await connection.DisposeAsync();
        var connectOutcome = firstConnect is null ? null : await Record.ExceptionAsync(() => firstConnect.WaitAsync(Run.Bound));

        var expectedState = firstConnectRunning ? AmiConnectionState.Connecting : AmiConnectionState.Initial;
        using (new AssertionScope())
        {
            stateAtStart.Should().Be(expectedState);
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "a start before the connection was established has nothing to load, and no reconnect will reload it")
                .Which.Message.Should().Be($"Not connected. Current state: {expectedState}",
                    "the start throws exactly what it throws today");
            Lines(serverLog, StateLoaded).Should().Be(0, "nothing was loaded");
            Warnings(serverLog).Should().BeEmpty("a start that throws is not an interrupted load");
            if (firstConnectRunning)
                connectOutcome.Should().NotBeNull("disposing the connection ends the connect that was still running");
        }
    }

    /// <summary>
    /// The reconnect loop's own connect attempt also reads <c>Connecting</c>, and the connection's state cannot tell it
    /// from a caller's first connect. A start that begins there throws, as it does today.
    /// </summary>
    [Fact]
    public async Task StartAsync_ShouldThrowNotConnected_WhenItBeginsWhileTheReconnectLoopIsConnecting()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, Close = PeerClose.AfterConnect };
        await using var run = await Run.ConnectAsync(first, autoReconnect: true);

        var attempting = await CompletesWithinBoundAsync(run.SecondSocketCreated);
        var stateAtStart = run.Connection.State;
        var outcome = await Record.ExceptionAsync(run.StartServerAsync);

        using (new AssertionScope())
        {
            attempting.Should().BeTrue("the reconnect loop started its connect attempt once Asterisk closed the session");
            stateAtStart.Should().Be(AmiConnectionState.Connecting, "the attempt waits for a banner the test never sends");
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "a start that begins in Connecting throws, as it does today")
                .Which.Message.Should().Be("Not connected. Current state: Connecting",
                    "the start throws exactly what it throws today");
            Lines(run.ServerLog, StateLoaded).Should().Be(0, "nothing was loaded");
            first.Fault.Should().BeNull("the peer served its session without failing");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The next timer the load creates on the manual clock, once the load has created it and is parked on it; or
    /// <see langword="null"/> when <paramref name="load"/> ends first, having waited for nothing.
    /// </summary>
    private static async Task<FakeTimeProvider.FakeTimer?> NextWaitOrEndAsync(Run run, Task load)
    {
        var next = run.Clock.TimersCreated.ReadAsync().AsTask();
        var first = await Task.WhenAny(next, load).WaitAsync(Run.Bound);
        return first == next ? await next : null;
    }

    /// <summary>
    /// Moves the manual clock through every wait the load parks on, one timer at a time and by exactly its due
    /// time, until the load ends or <see cref="MaxWaitsDriven"/> waits have passed. Returns each wait's due time.
    /// </summary>
    private static async Task<IReadOnlyList<TimeSpan>> DriveTheLoadAsync(Run run, Task load)
    {
        var waits = new List<TimeSpan>();
        while (waits.Count < MaxWaitsDriven && await NextWaitOrEndAsync(run, load) is { } timer)
        {
            waits.Add(timer.DueTime);
            run.Clock.Advance(timer.DueTime);
        }

        return waits;
    }

    /// <summary>How many lines of <paramref name="log"/> contain <paramref name="fragment"/>.</summary>
    private static int Lines(SignalingLogger<VerbaraServer> log, string fragment) =>
        log.Entries.Count(entry => entry.Line.Contains(fragment, StringComparison.Ordinal));

    /// <summary>The server's Warning lines, in order.</summary>
    private static List<string> Warnings(Run run) => Warnings(run.ServerLog);

    /// <summary>
    /// The Warning lines of <paramref name="log"/>, in order, apart from the announcement of a lost connection. That
    /// one is the connection's news, delivered on the thread pool while the load runs, so whether it is in the log yet
    /// is a race these tests do not own; they count what the load warns.
    /// </summary>
    private static List<string> Warnings(SignalingLogger<VerbaraServer> log) =>
        [.. log.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Line != VerbaraServerLog.AmiConnectionLostLine)
            .Select(entry => entry.Line)];

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            // Reaching the hang bound is the failure the caller asserts on, not an observation.
            return false;
        }
    }
}
