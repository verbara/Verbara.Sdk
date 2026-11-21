using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The public reading of a delivered loss no covering read has covered (<see cref="VerbaraServer.HasUnreconciledLoss"/>)
/// and the notice of each moment it turns true (<see cref="VerbaraServer.UnreconciledLossDelivered"/>): raised off the
/// connection's reader and outside any event dispatch, once per loss mark, its subscribers isolated from one another.
/// </summary>
/// <remarks>
/// The same rig as the coverage tests: a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> with an event buffer
/// of <see cref="Capacity"/>, a drop made by holding the dispatch and filling the buffer behind it, and delivered once a
/// tail event written after the release has been dispatched. A notice is read from a channel the handler writes to, so
/// every wait ends on a signal, bounded by <see cref="Run.Bound"/>.
/// </remarks>
public sealed partial class ChannelReadCoverageTests
{
    private const string NoticeSubscriberFailed = "[LIVE] An UnreconciledLossDelivered subscriber threw";

    // ---- The public reading ----

    [Fact]
    public async Task HasUnreconciledLoss_ShouldReadTrue_WhenADropIsDeliveredThroughAndNotYetCovered()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var beforeTheDrop = run.Server.HasUnreconciledLoss;

        using var hold = await HoldAndDropAsync(run, peer, "public-1");
        var behindTheBacklog = run.Server.HasUnreconciledLoss;
        await DeliverAsync(run, peer, hold, "public-1");

        using (new AssertionScope())
        {
            beforeTheDrop.Should().BeFalse("nothing was lost");
            behindTheBacklog.Should().BeFalse("an event received before the drop is still buffered: the drop is not delivered");
            run.Server.HasUnreconciledLoss.Should().BeTrue("the drop is delivered through and no read has covered it");
        }
    }

    [Fact]
    public async Task HasUnreconciledLoss_ShouldReadFalseAfterTheCall_WhenACoveringReconciliationRuns()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "public-2"))
            await DeliverAsync(run, peer, hold, "public-2");
        var before = run.Server.HasUnreconciledLoss;

        var span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            before.Should().BeTrue("premise: the delivered drop is unreconciled");
            Tag(span, Covered).Should().Be("True", "premise: the read covered");
            run.Server.HasUnreconciledLoss.Should().BeFalse("a covering reconciliation clears it");
        }
    }

    [Fact]
    public async Task HasUnreconciledLoss_ShouldStayTrue_WhenTheReconciliationDoesNotCover()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "public-3"))
            await DeliverAsync(run, peer, hold, "public-3");
        var before = run.Server.HasUnreconciledLoss;

        peer.StatusRefusedFromAsk = peer.Asked("Status") + 1;
        var span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            before.Should().BeTrue("premise: the delivered drop is unreconciled");
            Tag(span, Covered).Should().Be("False", "premise: a refused Status covers nothing");
            run.Server.HasUnreconciledLoss.Should().BeTrue("a reconciliation that did not cover leaves it true");
        }
    }

    [Fact]
    public async Task HasUnreconciledLoss_ShouldReadFalse_WhenTheServerRunsOverAConnectionThatIsNotTheSdksOwn()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        await using var substitute = new VerbaraServer(new PlainAmiConnectionWrapper(run.Connection),
            NullLogger<VerbaraServer>.Instance);
        await substitute.StartAsync().WaitAsync(Run.Bound);

        using (var hold = await HoldAndDropAsync(run, peer, "public-4"))
            await DeliverAsync(run, peer, hold, "public-4");

        using (new AssertionScope())
        {
            run.Server.HasUnreconciledLoss.Should().BeTrue("premise: the connection lost an event and delivered through it");
            substitute.HasUnreconciledLoss.Should().BeFalse("over a substitute connection no loss is ever reported");
        }
    }

    // ---- The notice ----

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedOnceNamingTheServer_WhenSeveralDropsAreDeliveredThroughTogether()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);

        using (var hold = await HoldAndDropAsync(run, peer, "notice-1"))
        {
            await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup("1790800000.8", "PJSIP/lost-00000008", "1790800000.8"));
            await FenceAsync(run);
            await DeliverAsync(run, peer, hold, "notice-1");
        }
        var first = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        // A further event delivered and read back: a second notice for the same drops would have had its turn.
        using (var hold = await HoldAsync(run, peer, "notice-1-after"))
            hold.Release();
        await FenceAsync(run);

        using (new AssertionScope())
        {
            first.Server.Should().BeSameAs(run.Server, "the notice names its server");
            first.Reading.Should().BeTrue("the reading is true when the notice is raised");
            notices.Reader.TryRead(out _).Should().BeFalse("the two drops were delivered through together: one notice");
            run.Server.LossNoticesPosted.Should().Be(1,
                "counted when the notice is decided, so a duplicate still on its way to the thread pool is seen too");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedAgain_WhenALaterDropIsDeliveredThrough()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);

        using (var hold = await HoldAndDropAsync(run, peer, "again-1"))
            await DeliverAsync(run, peer, hold, "again-1");
        var first = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);
        using (var hold = await HoldAndDropAsync(run, peer, "again-2"))
            await DeliverAsync(run, peer, hold, "again-2");
        var second = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            first.Reading.Should().BeTrue();
            second.Reading.Should().BeTrue("a later drop, delivered through with no reconciliation since, is announced again");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldNotBeRaised_WhileAnEventReceivedBeforeTheDropIsStillBuffered()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);

        var hold = await HoldAndDropAsync(run, peer, "backlog-1");
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup("1790800000.7", "PJSIP/lost-00000007", "1790800000.7"));
        await FenceAsync(run);
        var whileHeld = notices.Reader.TryRead(out _);
        var readingWhileHeld = run.Server.HasUnreconciledLoss;
        await DeliverAsync(run, peer, hold, "backlog-1");
        hold.Dispose();
        var once = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            whileHeld.Should().BeFalse("delivery has not passed the drops: nothing to reconcile yet");
            readingWhileHeld.Should().BeFalse();
            once.Reading.Should().BeTrue("premise: once delivery passes the drops, the notice comes");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedWithTheReadingTrue_WhenTheReloadAfterAReconnectEndsWithALossItDidNotCover()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var notices = Notices(run.Server);

        first.CloseSession();
        run.ReleaseSecondPeer();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        var notice = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            notice.Server.Should().BeSameAs(run.Server);
            notice.Reading.Should().BeTrue("the session change is a loss the refused reload did not cover");
            run.Server.HasUnreconciledLoss.Should().BeTrue("nothing has covered it since");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedOnlyByTheServerThatLostTheEvent_WhenTwoServersOfAPoolRun()
    {
        var lossyPeer = new BootingAsterisk { BootedAtLogin = true };
        var quietPeer = new BootingAsterisk { BootedAtLogin = true };
        await using var lossy = await StartAsync(lossyPeer);
        await using var quiet = await StartAsync(quietPeer);
        lossy.Server.PoolId = "n1";
        quiet.Server.PoolId = "n2";
        var fromLossy = Notices(lossy.Server);
        var fromQuiet = Notices(quiet.Server);

        using (var hold = await HoldAndDropAsync(lossy, lossyPeer, "pool-1"))
            await DeliverAsync(lossy, lossyPeer, hold, "pool-1");
        var notice = await fromLossy.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            notice.Server.Should().BeSameAs(lossy.Server);
            fromQuiet.Reader.TryRead(out _).Should().BeFalse("the other server lost nothing");
            quiet.Server.HasUnreconciledLoss.Should().BeFalse();
            lossy.Server.HasUnreconciledLoss.Should().BeTrue();
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldLetAReconciliationStartedFromItCover_WhenNoOtherReadIsInFlight()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var started = new TaskCompletionSource<Task<Activity>>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.UnreconciledLossDelivered += server =>
            started.TrySetResult(spans.TraceAsync(() => server.ReconcileChannelsAsync().AsTask()));

        using (var hold = await HoldAndDropAsync(run, peer, "from-notice-1"))
            await DeliverAsync(run, peer, hold, "from-notice-1");
        var span = await (await started.Task.WaitAsync(Run.Bound)).WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            Tag(span, Covered).Should().Be("True",
                "the notice runs outside any event dispatch, so the reconciliation waits for the backlog and covers");
            run.Server.HasUnreconciledLoss.Should().BeFalse("the drop is covered afterwards");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldHoldNoDelivery_WhenAHandlerDoesNotReturn()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        run.Server.UnreconciledLossDelivered += _ =>
        {
            entered.Set();
            release.Wait(Run.Bound);
        };

        using (var hold = await HoldAndDropAsync(run, peer, "held-notice-1"))
            await DeliverAsync(run, peer, hold, "held-notice-1");
        var handlerHeld = entered.Wait(Run.Bound);

        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var probe = run.Connection.Subscribe(new Observer(e =>
        {
            if (e is UserEventEvent { UserEvent: "after-held-notice" })
                later.TrySetResult();
        }));
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame("after-held-notice"));
        var delivered = await Record.ExceptionAsync(() => later.Task.WaitAsync(Run.Bound)) is null;
        var pingAnswered = await Record.ExceptionAsync(() => FenceAsync(run));
        release.Set();

        using (new AssertionScope())
        {
            handlerHeld.Should().BeTrue("premise: the handler is held");
            delivered.Should().BeTrue("a held notice handler delays no event delivery");
            pingAnswered.Should().BeNull("nor the connection's reading of its answers");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldNotBeRaised_WhenTheServersDisposalHasBegun()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        await using var witness = new VerbaraServer(run.Connection, NullLogger<VerbaraServer>.Instance);
        await witness.StartAsync().WaitAsync(Run.Bound);
        var fromDisposed = Notices(run.Server);
        var fromWitness = Notices(witness);

        var hold = await HoldAndDropAsync(run, peer, "disposed-1");
        await run.Server.DisposeAsync();
        await DeliverAsync(run, peer, hold, "disposed-1");
        hold.Dispose();
        await fromWitness.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        fromDisposed.Reader.TryRead(out _).Should().BeFalse(
            "the same signal reached the witness, and a server whose disposal has begun raises nothing");
    }

    // ---- A load of the server owns the loss while it runs ----

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldNotBeRaised_WhenTheReloadAfterAReconnectCoversTheLossItReads()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var notices = Notices(run.Server);

        first.CloseSession();
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await PendingNotificationsAsync(run);
        var settled = !run.Server.IsReconnectPending;
        var postedDuringTheReload = run.Server.LossNoticesPosted;
        var readingDuringTheReload = run.Server.HasUnreconciledLoss;
        var reload = run.Server.ReconnectReload;
        answer.SetResult();
        await reload.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            settled.Should().BeTrue("premise: the reconnect has settled while the reload's Status is unanswered");
            postedDuringTheReload.Should().Be(0, "the reload, still reading the channels, owns the session change");
            readingDuringTheReload.Should().BeFalse("a load of the server is running, and it reads the channels itself");
            run.Server.LossNoticesPosted.Should().Be(0, "the reload covered the session change: nothing to announce");
            notices.Reader.TryRead(out _).Should().BeFalse();
            second.Asked("Status").Should().Be(1, "the reload's Status is the only one");
            run.Server.HasUnreconciledLoss.Should().BeFalse("premise: the reload covered");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldStartNoReconciliation_WhenAHandlerFollowsTheGuideWhileTheReloadAfterAReconnectCovers()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var reconciliations = new ConcurrentQueue<Task>();
        run.Server.UnreconciledLossDelivered += server => reconciliations.Enqueue(server.ReconcileChannelsAsync().AsTask());

        first.CloseSession();
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await PendingNotificationsAsync(run);
        var reload = run.Server.ReconnectReload;
        answer.SetResult();
        await reload.WaitAsync(Run.Bound);
        await Task.WhenAll(reconciliations).WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            run.Server.LossNoticesPosted.Should().Be(0, "the reload covered the loss it read: no notice");
            reconciliations.Should().BeEmpty("no notice, so the handler started nothing");
            second.Asked("Status").Should().Be(1, "the reload's Status is the only one");
            run.Server.HasUnreconciledLoss.Should().BeFalse("the reload covered");
            run.Server.HasPendingLoss.Should().BeFalse("nothing overlapped the reload's read");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedOnceTheReloadEnds_WhenTheReloadAfterAReconnectDoesNotCover()
    {
        using var spans = new Spans();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var started = new TaskCompletionSource<(bool Reading, Task<Activity> Read)>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.UnreconciledLossDelivered += server =>
        {
            // The guide's handler. The reload's Status has been refused by now; the peer answers this one.
            second.StatusRefusedFromAsk = 0;
            started.TrySetResult((server.HasUnreconciledLoss, spans.TraceAsync(() => server.ReconcileChannelsAsync().AsTask())));
        };

        first.CloseSession();
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await PendingNotificationsAsync(run);
        var postedWhileTheReloadReads = run.Server.LossNoticesPosted;
        var reload = run.Server.ReconnectReload;
        answer.SetResult();
        await reload.WaitAsync(Run.Bound);
        var postedOnceTheReloadEnded = run.Server.LossNoticesPosted;
        var (readingAtTheNotice, read) = await started.Task.WaitAsync(Run.Bound);
        var span = await read.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            postedWhileTheReloadReads.Should().Be(0, "the reload has not ended: it still owns the loss");
            postedOnceTheReloadEnded.Should().Be(1, "the reload ended without covering: the end of the load announces it");
            readingAtTheNotice.Should().BeTrue("the notice finds the reading true");
            Tag(span, Covered).Should().Be("True", "no load runs any more, so the handler's reconciliation covers");
            Tag(span, Overlapped).Should().BeNull("the reload's read had ended before the notice");
            second.Asked("Status").Should().Be(2, "the refused reload's and the handler's");
            run.Server.HasUnreconciledLoss.Should().BeFalse("the handler's reconciliation covered");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedWhenTheReconnectSettles_WhenTheReloadEndedWithoutCoveringWhileItWasPending()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var notices = Notices(run.Server);
        using var hold = new HandlerHold();
        run.Connection.Reconnected += () => hold.Block();

        first.CloseSession();
        run.ReleaseSecondPeer();
        await hold.Held.WaitAsync(Run.Bound);

        // The server's handler ran before the host's: the reload is the server's, and it ends while the host's is held.
        var reload = run.Server.ReconnectReload;
        await reload.WaitAsync(Run.Bound);
        var pendingWhileHeld = run.Server.IsReconnectPending;
        var postedWhileHeld = run.Server.LossNoticesPosted;
        var readingWhileHeld = run.Server.HasUnreconciledLoss;
        hold.Release();
        await PendingNotificationsAsync(run);
        var postedOnceSettled = run.Server.LossNoticesPosted;
        var notice = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            second.Asked("Status").Should().Be(1, "premise: the reload sent its Status and it was refused");
            pendingWhileHeld.Should().BeTrue("premise: a handler of the reconnect has not returned");
            postedWhileHeld.Should().Be(0, "a pending reconnect owns the loss");
            readingWhileHeld.Should().BeFalse();
            postedOnceSettled.Should().Be(1, "the reconnect settled with a loss its reload did not cover");
            notice.Reading.Should().BeTrue();
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedOnceTheLoadEnds_WhenADropIsDeliveredWhileRequestInitialStateAsyncRuns()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusAnsweredAfter = answer.Task;
        peer.AnsweredAfter = new Dictionary<string, Task> { ["Agents"] = agents.Task };
        var atBarrier = BarrierSignal(run);
        var statusAsked = peer.Asked("Status") + 1;
        var agentsAsked = peer.Asked("Agents") + 1;
        var load = Task.Run(async () => await run.Server.RequestInitialStateAsync());
        await peer.AskedAtLeast("Status", statusAsked).WaitAsync(Run.Bound);

        // Lost after the load's Status was sent, so the load's sample does not hold it, and delivered while the load runs.
        using (var hold = await HoldAndDropAsync(run, peer, "during-load-1", fence: false))
        {
            answer.SetResult();
            await atBarrier.WaitAsync(Run.Bound);
            await DeliverAsync(run, peer, hold, "during-load-1");
        }
        await peer.AskedAtLeast("Agents", agentsAsked).WaitAsync(Run.Bound);
        var loadRunning = run.Server.IsLoadInFlight;
        var readingDuringTheLoad = run.Server.HasUnreconciledLoss;
        var postedDuringTheLoad = run.Server.LossNoticesPosted;
        agents.SetResult();
        await load.WaitAsync(Run.Bound);
        var postedOnceTheLoadEnded = run.Server.LossNoticesPosted;
        var notice = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            loadRunning.Should().BeTrue("premise: the load waits for its Agents answer");
            readingDuringTheLoad.Should().BeFalse("a load of the server is running");
            postedDuringTheLoad.Should().Be(0, "no notice while a load of the server runs");
            postedOnceTheLoadEnded.Should().Be(1, "the load ended without covering the drop delivered during it");
            notice.Reading.Should().BeTrue();
            run.Server.HasUnreconciledLoss.Should().BeTrue("nothing has covered the drop");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedOnceTheLoadEnds_WhenALoadCutShortByItsCallerLeavesADeliveredDrop()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusAnsweredAfter = answer.Task;
        peer.AnsweredAfter = new Dictionary<string, Task> { ["Agents"] = agents.Task };
        var atBarrier = BarrierSignal(run);
        var statusAsked = peer.Asked("Status") + 1;
        var agentsAsked = peer.Asked("Agents") + 1;
        using var cts = new CancellationTokenSource();
        var load = Task.Run(async () => await run.Server.RequestInitialStateAsync(cts.Token));
        await peer.AskedAtLeast("Status", statusAsked).WaitAsync(Run.Bound);

        // Lost after the load's Status was sent and delivered while the load runs; the caller then cuts the load short while
        // it waits for its Agents answer, so the load ends by an exception, never having transferred its read.
        using (var hold = await HoldAndDropAsync(run, peer, "cut-1", fence: false))
        {
            answer.SetResult();
            await atBarrier.WaitAsync(Run.Bound);
            await DeliverAsync(run, peer, hold, "cut-1");
        }
        await peer.AskedAtLeast("Agents", agentsAsked).WaitAsync(Run.Bound);
        var loadRunning = run.Server.IsLoadInFlight;
        var readingDuringTheLoad = run.Server.HasUnreconciledLoss;
        var postedDuringTheLoad = run.Server.LossNoticesPosted;
        cts.Cancel();
        var thrown = await Record.ExceptionAsync(() => load.WaitAsync(Run.Bound));
        var postedOnceTheLoadEnded = run.Server.LossNoticesPosted;
        agents.SetResult();
        // Waited for only once posted: a notice that was never posted fails the count below, not the hang bound.
        (VerbaraServer Server, bool Reading)? notice = postedOnceTheLoadEnded == 0
            ? null
            : await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            loadRunning.Should().BeTrue("premise: the load waits for its Agents answer");
            readingDuringTheLoad.Should().BeFalse("a load of the server is running");
            postedDuringTheLoad.Should().Be(0, "no notice while a load of the server runs");
            thrown.Should().BeAssignableTo<OperationCanceledException>("premise: the caller's token cut the load short");
            postedOnceTheLoadEnded.Should().Be(1, "a load that ends by an exception announces the loss it leaves, as one that completes does");
            notice.Should().NotBeNull("the posted notice reaches the subscriber");
            notice?.Server.Should().BeSameAs(run.Server);
            notice?.Reading.Should().BeTrue("the notice finds the reading true");
            run.Server.HasUnreconciledLoss.Should().BeTrue("nothing has covered the drop");
            run.Server.LossNoticesPosted.Should().Be(1, "one notice per loss");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldNotBeRaisedAgain_WhenALoadEndsWithTheLossAlreadyAnnounced()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);
        using (var hold = await HoldAndDropAsync(run, peer, "announced-1"))
            await DeliverAsync(run, peer, hold, "announced-1");
        var first = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);
        var postedBefore = run.Server.LossNoticesPosted;

        peer.StatusRefusedFromAsk = peer.Asked("Status") + 1;
        await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            first.Reading.Should().BeTrue();
            postedBefore.Should().Be(1, "premise: the delivered drop was announced");
            run.Server.HasUnreconciledLoss.Should().BeTrue("premise: the load's Status was refused, so it covered nothing");
            run.Server.LossNoticesPosted.Should().Be(1, "the end of the load found the same loss mark: one notice per loss");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldNotAnnounceAnAnnouncedLossAgain_WhenAnEvaluationThatReadItDecidesAfterANewerLossWasAnnounced()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);
        using (var hold = await HoldAndDropAsync(run, peer, "stale-1"))
            await DeliverAsync(run, peer, hold, "stale-1");
        _ = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        // The next evaluation that reads the reading true is held before it decides: the end of the refused load below,
        // on a thread-pool thread. While the load ran the reading was false, so no other evaluation gets this far first.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var armed = 1;
        run.Server.LossNoticeDeciding = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
                return;
            held.TrySetResult();
            release.Wait(Run.Bound + Run.Bound);
        };
        peer.StatusRefusedFromAsk = peer.Asked("Status") + 1;
        var load = run.Server.RequestInitialStateAsync().AsTask();
        var heldAtTheLoadsEnd = await Record.ExceptionAsync(() => held.Task.WaitAsync(Run.Bound)) is null;

        // A newer drop, delivered and announced while the load's evaluation is held.
        using (var hold = await HoldAndDropAsync(run, peer, "stale-2"))
            await DeliverAsync(run, peer, hold, "stale-2");
        var newer = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);
        release.Set();
        await load.WaitAsync(Run.Bound);
        var postedOnceTheHeldLoadEnded = run.Server.LossNoticesPosted;

        // A later load that covers nothing ends with the newer loss already announced.
        peer.StatusRefusedFromAsk = peer.Asked("Status") + 1;
        await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            heldAtTheLoadsEnd.Should().BeTrue("premise: the end of the refused load read the first loss true and is held");
            newer.Reading.Should().BeTrue("premise: the newer drop is announced while the load's evaluation is held");
            postedOnceTheHeldLoadEnded.Should().Be(2,
                "posted 2: the first loss and the newer one, each once; the held evaluation read the first loss, already announced");
            run.Server.LossNoticesPosted.Should().Be(2, "a later load ending over the newer, announced loss announces nothing");
            run.Server.HasUnreconciledLoss.Should().BeTrue("premise: nothing has covered the newer drop");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldLetTheReloadEnd_WhenAHandlerOfANoticeTheReloadsEndRaisedDoesNotReturn()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        run.Server.UnreconciledLossDelivered += _ =>
        {
            entered.TrySetResult();
            release.Wait(Run.Bound + Run.Bound);
        };

        first.CloseSession();
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await PendingNotificationsAsync(run);
        var reload = run.Server.ReconnectReload;
        answer.SetResult();
        var handlerHeld = await Record.ExceptionAsync(() => entered.Task.WaitAsync(Run.Bound)) is null;
        var reloadEnded = await Record.ExceptionAsync(() => reload.WaitAsync(Run.Bound)) is null;
        release.Set();

        using (new AssertionScope())
        {
            handlerHeld.Should().BeTrue("premise: the end of the reload raised the notice and its handler is held");
            reloadEnded.Should().BeTrue("the notice is posted to the thread pool, so a held handler does not hold the reload");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldLetTheConnectionsNotificationsGoOn_WhenAHandlerOfANoticeTheSettleRaisedDoesNotReturn()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        run.Server.UnreconciledLossDelivered += _ =>
        {
            entered.TrySetResult();
            release.Wait(Run.Bound + Run.Bound);
        };
        using var hold = new HandlerHold();
        run.Connection.Reconnected += () => hold.Block();

        first.CloseSession();
        run.ReleaseSecondPeer();
        await hold.Held.WaitAsync(Run.Bound);
        await run.Server.ReconnectReload.WaitAsync(Run.Bound);
        var postedWhileHeld = run.Server.LossNoticesPosted;
        hold.Release();
        var handlerHeld = await Record.ExceptionAsync(() => entered.Task.WaitAsync(Run.Bound)) is null;
        var notificationsWentOn = await Record.ExceptionAsync(() => PendingNotificationsAsync(run)) is null;
        release.Set();

        using (new AssertionScope())
        {
            postedWhileHeld.Should().Be(0, "premise: the reload ended while the reconnect was pending");
            handlerHeld.Should().BeTrue("premise: the settle raised the notice and its handler is held");
            notificationsWentOn.Should().BeTrue(
                "the notice is posted to the thread pool, so a held handler does not hold the connection's notifications");
        }
    }

    // ---- Subscriber isolation ----

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldStillTellTheOtherSubscribersAndCountTheFailure_WhenOneThrows()
    {
        using var faults = new FaultCount();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.UnreconciledLossDelivered += _ => throw new InvalidOperationException("first subscriber");
        run.Server.UnreconciledLossDelivered += _ => second.TrySetResult();
        run.Server.UnreconciledLossDelivered += _ => third.TrySetResult();

        using (var hold = await HoldAndDropAsync(run, peer, "throws-1"))
            await DeliverAsync(run, peer, hold, "throws-1");
        await third.Task.WaitAsync(Run.Bound);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Ghost, GhostChannel, Ghost));
        await FenceAsync(run);
        var failures = run.ServerLog.Entries.Where(e => e.Line.Contains(NoticeSubscriberFailed, StringComparison.Ordinal)).ToList();

        using (new AssertionScope())
        {
            second.Task.IsCompletedSuccessfully.Should().BeTrue("the subscriber after the one that threw is still told");
            failures.Should().ContainSingle("the failure is reported once").Which.Level.Should().Be(LogLevel.Warning);
            faults.Value.Should().Be(1, "the failure is counted once");
            run.Server.Channels.GetByUniqueId(Ghost).Should().NotBeNull("the server keeps delivering events");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldStillTellTheLaterSubscribers_WhenOneThrowsAndTheLoggerThrowsOnItsReport()
    {
        using var faults = new FaultCount();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        await using var server = new VerbaraServer(run.Connection, new ThrowingOnWarningLogger());
        await server.StartAsync().WaitAsync(Run.Bound);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.UnreconciledLossDelivered += _ => throw new InvalidOperationException("first subscriber");
        server.UnreconciledLossDelivered += _ => later.TrySetResult();

        using (var hold = await HoldAndDropAsync(run, peer, "logger-throws-1"))
            await DeliverAsync(run, peer, hold, "logger-throws-1");
        await later.Task.WaitAsync(Run.Bound);

        faults.Value.Should().Be(1, "the failure is counted before the report the logger fails on");
    }

    // ---- The rig ----

    private static Channel<(VerbaraServer Server, bool Reading)> Notices(VerbaraServer server)
    {
        var notices = Channel.CreateUnbounded<(VerbaraServer Server, bool Reading)>();
        server.UnreconciledLossDelivered += s => notices.Writer.TryWrite((s, s.HasUnreconciledLoss));
        return notices;
    }

    /// <summary>A logger whose Warning lines throw, as a host's broken logging pipeline may.</summary>
    private sealed class ThrowingOnWarningLogger : ILogger<VerbaraServer>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                throw new InvalidOperationException("the host's logger failed");
        }
    }

    /// <summary>The measurements of <c>live.server.subscriber_faults</c> taken while it is alive.</summary>
    private sealed class FaultCount : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _value;

        public FaultCount()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Live" && instrument.Name == "live.server.subscriber_faults")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref _value, measurement));
            _listener.Start();
        }

        public long Value => Interlocked.Read(ref _value);

        public void Dispose() => _listener.Dispose();
    }
}
