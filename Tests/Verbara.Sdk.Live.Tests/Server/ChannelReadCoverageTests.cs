using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A lost event — one the full event buffer refused and no action had received — is pending for the live server from the
/// refusal on, and only a channel read that can prove it saw past it covers it: one that sampled the loss count before it
/// sent <c>Status</c>, completed on the session it began on, reached its barrier (not timed out, not skipped inside a
/// dispatch), kept no channel whose creation event was received before the send, and was overlapped by no other channel
/// read of the server. Each read's <c>live channel-reconcile</c> span says whether it covered
/// (<c>live.reconcile.covered</c>) and, when it did not, why.
/// </summary>
/// <remarks>
/// <para>
/// In process and deterministic: a real <see cref="AmiConnection"/> with an event buffer of <see cref="Capacity"/> over
/// <see cref="Run"/>, on the run's manual clock. A drop is made by holding the dispatch on a <see cref="DispatchGate"/>,
/// filling the buffer behind it and writing one more event no action asked for; a Ping the peer answers is the fence that
/// says the reader has handed it to the buffer. A drop is delivered once a tail event written after the release has been
/// dispatched. No wall clock is read except <see cref="Run.Bound"/>, a hang bound every wait ends long before.
/// </para>
/// <para>
/// The spans are read through an <see cref="ActivityListener"/> and attributed by parent: each read under test runs
/// inside an activity of this class's own source, so a span of another test running in parallel is never read.
/// </para>
/// </remarks>
public sealed class ChannelReadCoverageTests
{
    private const int Capacity = 2;
    private const string ReconcileSpan = "live channel-reconcile";
    private const string Covered = "live.reconcile.covered";
    private const string KeptStale = "live.reconcile.kept_stale";
    private const string Overlapped = "live.reconcile.overlapped";
    private const string Deferred = "live.reconcile.deferred";
    private const string Refused = "live.status.refused";
    private const string StateLoaded = "[LIVE] State loaded";
    private const string Ghost = "1790800000.71";
    private const string GhostChannel = "PJSIP/ghost-00000071";

    private static readonly ActivitySource TestSource = new("Verbara.Sdk.Live.Tests.ChannelReadCoverage");

    // ---- Publication: a full read after the drop is delivered covers it ----

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldCoverTheLossAndTagItsSpanCovered_WhenItCompletesInFullAfterTheDropIsDelivered()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var beforeDrop = Read(run);

        using var hold = await HoldAndDropAsync(run, peer, "cover-1");
        var whileBehindTheBacklog = Read(run);
        await DeliverAsync(run, peer, hold, "cover-1");
        var onceDelivered = Read(run);

        var span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        var afterTheRead = Read(run);

        using (new AssertionScope())
        {
            beforeDrop.Should().Be(new Reading(Complete: true, Covered: true, Pending: false, Undelivered: false, Delivered: false),
                "the start's read covered a table no event was lost from");
            whileBehindTheBacklog.Should().Be(new Reading(Complete: true, Covered: false, Pending: true, Undelivered: true, Delivered: false),
                "a loss is pending from the refusal itself, while the event received before it waits in the buffer; a drop does "
                + "not change completeness");
            onceDelivered.Should().Be(new Reading(Complete: true, Covered: false, Pending: true, Undelivered: false, Delivered: true),
                "once the pump has dispatched through the drop the loss is delivered, and still uncovered");
            afterTheRead.Should().Be(new Reading(Complete: true, Covered: true, Pending: false, Undelivered: false, Delivered: false),
                "a full read whose sample counted the drop covers it");
            Tag(span, Covered).Should().Be("True", "the read met every condition, and its span says so");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldKeepTheLossPending_WhenTheDropHappensAfterItsStatusWasSent()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var first = await HoldAndDropAsync(run, peer, "after-send-1"))
            await DeliverAsync(run, peer, first, "after-send-1");

        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusAnsweredAfter = answer.Task;
        var atBarrier = BarrierSignal(run);
        var asked = peer.Asked("Status") + 1;
        var reconcile = spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        await peer.AskedAtLeast("Status", asked).WaitAsync(Run.Bound);

        // Sent, then lost: the second drop reaches the reader before the Status answer, so before StatusComplete.
        using var second = await HoldAndDropAsync(run, peer, "after-send-2", fence: false);
        answer.SetResult();
        await atBarrier.WaitAsync(Run.Bound);
        await DeliverAsync(run, peer, second, "after-send-2");
        var span = await reconcile.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            run.Server.HasPendingLoss.Should().BeTrue(
                "the read sampled the loss count before it sent Status, and the drop after the send is not in that sample");
            run.Server.ChannelTableCovered.Should().BeFalse("the loss after the send is not covered");
            run.Server.HasDeliveredUnreconciledLoss.Should().BeTrue("it was delivered and is still uncovered");
            Tag(span, Covered).Should().Be("True",
                "the read itself met every condition: it covered what it sampled, the earlier drop");
        }
    }

    // ---- What publishes nothing ----

    [Theory]
    [InlineData("refused")]
    [InlineData("cancelled")]
    [InlineData("session-ended")]
    public async Task ReconcileChannelsAsync_ShouldPublishNothing_WhenTheReadDoesNotComplete(string how)
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "incomplete-" + how))
            await DeliverAsync(run, peer, hold, "incomplete-" + how);
        var pendingBefore = run.Server.HasPendingLoss;

        Activity span;
        switch (how)
        {
            case "refused":
                peer.StatusRefusedFromAsk = peer.Asked("Status") + 1;
                span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
                break;
            case "cancelled":
            {
                var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                peer.StatusAnsweredAfter = answer.Task;
                using var stop = new CancellationTokenSource();
                var asked = peer.Asked("Status") + 1;
                var read = spans.TraceAsync(() => run.Server.ReconcileChannelsAsync(stop.Token).AsTask(), expectThrow: true);
                await peer.AskedAtLeast("Status", asked).WaitAsync(Run.Bound);
                await stop.CancelAsync();
                answer.SetResult();
                span = await read.WaitAsync(Run.Bound);
                break;
            }
            default:
                peer.Close = PeerClose.DuringStatus;
                peer.StatusChannelsBeforeClose = 0;
                span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask(), expectThrow: true);
                break;
        }

        using (new AssertionScope())
        {
            pendingBefore.Should().BeTrue("the delivered drop is pending before the read");
            run.Server.ChannelTableCovered.Should().BeFalse("a read that did not complete covers nothing");
            if (how != "session-ended")
                run.Server.HasPendingLoss.Should().BeTrue("a " + how + " read publishes no sample");
            Tag(span, Covered).Should().Be("False", "a " + how + " read covers nothing, and its span says so");
            if (how == "refused")
                Tag(span, Refused).Should().Be(BootingAsterisk.PermissionDenied, "the refusal is the reason");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldPublishNothingAndTagItsSpanDeferred_WhenItsBarrierRunsOut()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "deferred-1"))
            await DeliverAsync(run, peer, hold, "deferred-1");

        using var wedge = await HoldAsync(run, peer, "deferred-2");
        var read = spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        while (!read.IsCompleted)
        {
            using var stop = new CancellationTokenSource();
            var next = run.Clock.TimersCreated.ReadAsync(stop.Token).AsTask();
            var first = await Task.WhenAny(read, next).WaitAsync(Run.Bound);
            await stop.CancelAsync();
            if (first == next && next.IsCompletedSuccessfully) run.Clock.Advance((await next).DueTime);
        }
        var span = await read.WaitAsync(Run.Bound);
        wedge.Release();

        using (new AssertionScope())
        {
            run.Server.HasPendingLoss.Should().BeTrue("a read whose barrier ran out proves nothing about the backlog");
            run.Server.ChannelTableCovered.Should().BeFalse();
            Tag(span, Covered).Should().Be("False");
            Tag(span, Deferred).Should().Be("Status", "the deferral is the reason, as today's tag already says");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldPublishNothingAndTagItsSpanKeptStale_WhenAChannelReceivedBeforeTheSendIsKept()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "stale-1"))
            await DeliverAsync(run, peer, hold, "stale-1");

        // The ghost's Newchannel is received before Status is sent and waits behind a held handler; the snapshot omits it.
        using var wedge = await HoldAsync(run, peer, "stale-2");
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Ghost, GhostChannel, Ghost));
        await FenceAsync(run);
        peer.StatusChannels = [];
        var atBarrier = BarrierSignal(run);
        var read = spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        await atBarrier.WaitAsync(Run.Bound);
        wedge.Release();
        var span = await read.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            run.Server.Channels.GetByUniqueId(Ghost).Should().NotBeNull(
                "premise: the channel was admitted above the read's window, so the read kept it");
            run.Server.HasPendingLoss.Should().BeTrue("a read that kept a channel it could not prove covers nothing");
            run.Server.ChannelTableCovered.Should().BeFalse();
            Tag(span, Covered).Should().Be("False");
            Tag(span, KeptStale).Should().Be("1", "one channel was kept whose creation event preceded the send");
        }
    }

    // ---- Overlapping reads ----

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldKeepTheGhostAndPublishNothing_WhenAnOlderReadAdmitsItAboveTheNewerReadsWindow()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "retain-1"))
            await DeliverAsync(run, peer, hold, "retain-1");

        using var admitted = new ManualResetEventSlim();
        using var releaseAdmission = new ManualResetEventSlim();
        run.Server.Channels.ChannelAdded += c =>
        {
            if (c.UniqueId != Ghost || admitted.IsSet) return;
            admitted.Set();
            releaseAdmission.Wait(Run.Bound);
        };

        // R: its snapshot lists G, a channel the table never held, and it waits at its barrier behind a held handler.
        using var wedge = await HoldAsync(run, peer, "retain-2");
        peer.StatusChannels = [new StatusChannel(Ghost, GhostChannel, LinkedId: Ghost)];
        var rAtBarrier = BarrierSignal(run);
        var older = spans.TraceAsync(() => Task.Run(async () => await run.Server.ReconcileChannelsAsync()));
        await rAtBarrier.WaitAsync(Run.Bound);

        // A: counted and its window opened before R admits anything; its snapshot omits G.
        var answerA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusChannels = [];
        peer.AnsweredAfter = new Dictionary<string, Task> { ["Status"] = answerA.Task };
        var asked = peer.Asked("Status") + 1;
        var newer = spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        await peer.AskedAtLeast("Status", asked).WaitAsync(Run.Bound);

        // R reaches its barrier, admits G above A's mark and is held in its ChannelAdded subscriber while A completes.
        wedge.Release();
        admitted.Wait(Run.Bound).Should().BeTrue("premise: R admits G");
        answerA.SetResult();
        var spanA = await newer.WaitAsync(Run.Bound);
        var ghostKeptByA = run.Server.Channels.GetByUniqueId(Ghost) is not null;
        var pendingAfterA = run.Server.HasPendingLoss;
        releaseAdmission.Set();
        var spanR = await older.WaitAsync(Run.Bound);

        // B: overlapped by none, removes G and covers.
        peer.AnsweredAfter = new Dictionary<string, Task>();
        var spanB = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            ghostKeptByA.Should().BeTrue("premise: A keeps G, admitted above its window's mark");
            pendingAfterA.Should().BeTrue("A overlapped R, which was still able to admit: A covers nothing");
            Tag(spanA, Covered).Should().Be("False");
            Tag(spanA, Overlapped).Should().Be("True", "R was in flight when A was counted");
            Tag(spanR, Covered).Should().Be("False", "A was counted before R began to apply");
            run.Server.Channels.GetByUniqueId(Ghost).Should().BeNull("B, overlapped by none, removes G");
            Tag(spanB, Covered).Should().Be("True");
            run.Server.HasPendingLoss.Should().BeFalse("B covers the loss");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldRemoveTheGhostAndStillPublishNothing_WhenAnOlderReadAdmittedItBeforeTheNewerReadsWindow()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "benign-1"))
            await DeliverAsync(run, peer, hold, "benign-1");

        // R admits G and is held, still in flight, in its own completion's raise: its ChannelAdded has been delivered, so
        // A's removal of G waits for no earlier notification.
        using var held = new ManualResetEventSlim();
        using var releaseR = new ManualResetEventSlim();
        var holdR = 1;
        run.Server.ChannelSnapshotCompleted += _ =>
        {
            if (Interlocked.Exchange(ref holdR, 0) == 0) return;
            held.Set();
            releaseR.Wait(Run.Bound);
        };
        peer.StatusChannels = [new StatusChannel(Ghost, GhostChannel, LinkedId: Ghost)];
        var older = spans.TraceAsync(() => Task.Run(async () => await run.Server.ReconcileChannelsAsync()));
        held.Wait(Run.Bound).Should().BeTrue("premise: R has applied its snapshot and is still in flight");
        var ghostAdmittedByR = run.Server.Channels.GetByUniqueId(Ghost) is not null;

        // R's apply began before any other read was counted, so R covered the first drop, and published before it raised
        // its completion. A loss after R's sample is what A, overlapped, must leave pending.
        var pendingWhileRIsHeld = run.Server.HasPendingLoss;
        using (var hold = await HoldAndDropAsync(run, peer, "benign-2"))
            await DeliverAsync(run, peer, hold, "benign-2");

        peer.StatusChannels = [];
        var spanA = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        var ghostAfterA = run.Server.Channels.GetByUniqueId(Ghost);
        var pendingAfterA = run.Server.HasPendingLoss;
        releaseR.Set();
        var spanR = await older.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            ghostAdmittedByR.Should().BeTrue("premise: R admitted G before A opened its window");
            pendingWhileRIsHeld.Should().BeFalse("premise: R covered the first drop before it raised its completion");
            ghostAfterA.Should().BeNull("G was admitted at or below A's mark, so A removes it");
            pendingAfterA.Should().BeTrue("A overlapped R and covers nothing, though the ghost is gone one read earlier");
            Tag(spanA, Covered).Should().Be("False");
            Tag(spanA, Overlapped).Should().Be("True");
            Tag(spanR, Covered).Should().Be("True", "no read was counted before R began to apply");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldLeaveTheFirstReadCovered_WhenAChannelRemovedSubscriberStartsASecondReadDuringItsApply()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Ghost, GhostChannel, Ghost));
        using (var hold = await HoldAndDropAsync(run, peer, "pin-1"))
            await DeliverAsync(run, peer, hold, "pin-1");
        run.Server.Channels.GetByUniqueId(Ghost).Should().NotBeNull("premise: the table holds the channel");

        Task<Activity>? second = null;
        run.Server.Channels.ChannelRemoved += c =>
        {
            if (c.UniqueId != Ghost || second is not null) return;
            second = spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        };
        peer.StatusChannels = [];
        var spanA = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
        second.Should().NotBeNull("premise: A's removal started B");
        var spanB = await second!.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            Tag(spanA, Covered).Should().Be("True",
                "B was raised after A's apply began: it sent its Status after A's StatusComplete, so A's sample stands");
            Tag(spanB, Covered).Should().Be("False");
            Tag(spanB, Overlapped).Should().Be("True", "B saw A in flight when it was raised");
            run.Server.HasPendingLoss.Should().BeFalse("A covered the drop");
        }
    }

    // ---- Around a session start ----

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldPublishNothing_WhenItStraddlesASessionStart()
    {
        using var spans = new Spans();
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        using var applying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        run.Server.Channels.ChannelAdded += c =>
        {
            if (c.UniqueId != "old") return;
            applying.Set();
            release.Wait(Run.Bound);
        };
        first.StatusChannels = [new StatusChannel("old", "PJSIP/old", LinkedId: "call")];
        var read = spans.TraceAsync(() => Task.Run(async () => await run.Server.ReconcileChannelsAsync()));
        applying.Wait(Run.Bound).Should().BeTrue();
        run.ReleaseSecondPeer();
        first.CloseSession();
        await second.AskedAtLeast("Agents", 1).WaitAsync(Run.Bound);
        release.Set();
        var span = await read.WaitAsync(Run.Bound);
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            run.Server.HasPendingLoss.Should().BeTrue(
                "the session change is a loss; the old session's read cannot cover it and the reload's Status was refused");
            run.Server.ChannelTableCovered.Should().BeFalse();
            Tag(span, Covered).Should().Be("False", "a read that began on the lost session covers nothing");
        }
    }

    [Fact]
    public async Task Reload_ShouldReadTheSessionChangeUndeliveredWhileTheReconnectIsPending_AndCoverItOnlyOnceItsReadPublishes()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var beforeTheLoss = Read(run);
        using var hold = new HandlerHold();
        run.Server.ConnectionLost += _ => hold.Block();

        first.CloseSession();
        await hold.Held.WaitAsync(Run.Bound);
        run.ReleaseSecondPeer();
        await run.ConnectionLog.Logged("[AMI] Connected", 2).WaitAsync(Run.Bound);
        var pendingReconnect = run.Server.IsReconnectPending;
        var inTheWindow = Read(run);
        var statusAskedInTheWindow = second.Asked("Status");

        hold.Release();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await PendingNotificationsAsync(run);
        var reconnectPendingAfter = run.Server.IsReconnectPending;
        var reloadReading = Read(run);

        answer.SetResult();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        var afterTheReload = Read(run);

        using (new AssertionScope())
        {
            beforeTheLoss.Should().Be(new Reading(Complete: true, Covered: true, Pending: false, Undelivered: false, Delivered: false));
            pendingReconnect.Should().BeTrue("premise: the lost session's handler holds the reconnect's notice");
            statusAskedInTheWindow.Should().Be(0, "premise: the reload has not started");
            inTheWindow.Should().Be(new Reading(Complete: false, Covered: false, Pending: true, Undelivered: true, Delivered: false),
                "while the reconnect is pending the session change is a loss pending and undelivered, whatever the new pump "
                + "has dispatched");
            reconnectPendingAfter.Should().BeFalse("premise: the reload's handlers have returned");
            reloadReading.Should().Be(new Reading(Complete: false, Covered: false, Pending: true, Undelivered: false, Delivered: true),
                "once the handlers have returned the session change is delivered, and the reload's read is still open");
            afterTheReload.Should().Be(new Reading(Complete: true, Covered: true, Pending: false, Undelivered: false, Delivered: false),
                "the reload's read completed on the new session and published");
        }
    }

    // ---- The three routes publish ----

    [Fact]
    public async Task StartAsync_ShouldCoverALossCountedBeforeTheFirstLoad_WhenItsReadCompletesInFull()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: Capacity);
        using (var hold = await HoldAndDropAsync(run, peer, "load-1"))
            await DeliverAsync(run, peer, hold, "load-1");
        var pendingBeforeTheLoad = run.Server.HasPendingLoss;

        await run.StartServerAsync();

        using (new AssertionScope())
        {
            pendingBeforeTheLoad.Should().BeTrue("with no covering read yet, any loss is pending");
            run.Server.HasPendingLoss.Should().BeFalse("the load's channel read covers it");
            run.Server.ChannelTableCovered.Should().BeTrue();
        }
    }

    // ---- The reading point: the current pump, never the dispatch frame ----

    [Fact]
    public async Task ChannelTableCovered_ShouldReadTheCurrentCount_WhenReadInsideTheObserverOfAnEventReceivedBeforeTheDrop()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var seen = new TaskCompletionSource<(bool Covered, long Current, long Arrival)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var probe = run.Connection.Subscribe(new Observer(e =>
        {
            if (e is UserEventEvent { UserEvent: "probe-onnext" })
                seen.TrySetResult((run.Server.ChannelTableCovered, run.Server.ReadCurrentEventLossEpoch().DroppedBefore,
                    run.Server.ReadEventLossEpoch().DroppedBefore));
        }));

        using var hold = await HoldAsync(run, peer, "point-1");
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame("probe-onnext"));
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame("point-fill"));
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup("1790800000.99", "PJSIP/lost-00000099", "1790800000.99"));
        await FenceAsync(run);
        hold.Release();
        var (covered, current, arrival) = await seen.Task.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            arrival.Should().Be(0, "premise: the event arrived before the drop, and its own epoch says so");
            current.Should().Be(1, "the current reader reads the current pump's count, never the dispatch frame's");
            covered.Should().BeFalse("the drop is pending although the event being delivered arrived before it");
        }
    }

    [Fact]
    public async Task ChannelTableCovered_ShouldReadFalseBeforeAndAfterAnInHandlerReconciliation_WhenReadInsideTheOnEventHandlerOfAnEventReceivedBeforeTheDrop()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var readings = new ConcurrentQueue<(bool Covered, long Current, long Arrival)>();
        var done = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Connection.OnEvent += async e =>
        {
            if (e is not UserEventEvent { UserEvent: "probe-onevent" }) return;
            readings.Enqueue((run.Server.ChannelTableCovered, run.Server.ReadCurrentEventLossEpoch().DroppedBefore,
                run.Server.ReadEventLossEpoch().DroppedBefore));
            var span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());
            readings.Enqueue((run.Server.ChannelTableCovered, run.Server.ReadCurrentEventLossEpoch().DroppedBefore,
                run.Server.ReadEventLossEpoch().DroppedBefore));
            done.TrySetResult(span);
        };

        using var hold = await HoldAsync(run, peer, "point-2");
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame("probe-onevent"));
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame("point-fill"));
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup("1790800000.98", "PJSIP/lost-00000098", "1790800000.98"));
        await FenceAsync(run);
        hold.Release();
        var inHandler = await done.Task.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            readings.Should().Equal([(false, 1L, 0L), (false, 1L, 0L)],
                "inside the handler of an event received before the drop the table is uncovered and the current count is "
                + "read; the arrival-time epoch stays the event's own; the reconciliation the handler ran itself skipped its "
                + "barrier and covers nothing");
            Tag(inHandler, Covered).Should().Be("False");
            new[] { Tag(inHandler, Deferred), Tag(inHandler, Refused), Tag(inHandler, KeptStale), Tag(inHandler, Overlapped) }
                .Should().OnlyContain(t => t == null, "a read skipped in dispatch reads false with no other reason");
        }
    }

    [Fact]
    public async Task ChannelSnapshotCompleted_ShouldFindTheTableCovered_WhenRaisedByACoveringRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "order-1"))
            await DeliverAsync(run, peer, hold, "order-1");
        var coveredBefore = run.Server.ChannelTableCovered;
        var inTheRaise = new ConcurrentQueue<bool>();
        run.Server.ChannelSnapshotCompleted += _ => inTheRaise.Enqueue(run.Server.ChannelTableCovered);

        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            coveredBefore.Should().BeFalse("premise: the delivered drop leaves the table uncovered");
            inTheRaise.Should().Equal([true], "the covered epoch is published before the completion is raised");
        }
    }

    // ---- The rig ----

    private sealed record Reading(bool Complete, bool Covered, bool Pending, bool Undelivered, bool Delivered);

    private static Reading Read(Run run) => new(run.Server.ChannelTableComplete, run.Server.ChannelTableCovered,
        run.Server.HasPendingLoss, run.Server.HasUndeliveredLoss, run.Server.HasDeliveredUnreconciledLoss);

    private static string? Tag(Activity span, string key) =>
        span.GetTagItem(key) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static async Task<Run> StartAsync(BootingAsterisk peer)
    {
        var run = await Run.ConnectAsync(peer, eventPumpCapacity: Capacity);
        try
        {
            await run.StartServerAsync();
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    private static async Task FenceAsync(Run run) =>
        _ = await run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);

    private static Task BarrierSignal(Run run)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.FenceWaitObserved = (_, _) => signal.TrySetResult();
        return signal.Task;
    }

    /// <summary>Holds the dispatch on the peer's <c>UserEvent</c> <paramref name="name"/>.</summary>
    private static async Task<Hold> HoldAsync(Run run, BootingAsterisk peer, string name)
    {
        var hold = new Hold(run.Connection, name);
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame(name));
        await hold.Gate.Held.WaitAsync(Run.Bound);
        return hold;
    }

    /// <summary>
    /// Holds the dispatch, fills the buffer behind it and writes one <c>Hangup</c> no action asked for, which the full
    /// buffer refuses: one lost event. With <paramref name="fence"/>, a Ping says the reader has handed it to the buffer.
    /// </summary>
    private static async Task<Hold> HoldAndDropAsync(Run run, BootingAsterisk peer, string name, bool fence = true)
    {
        var hold = await HoldAsync(run, peer, name);
        for (var i = 0; i < Capacity; i++)
            await peer.SendEventAsync("UserEvent", DispatchGate.Frame(name + "-fill"));
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup("1790800000.9", "PJSIP/lost-00000009", "1790800000.9"));
        if (fence)
            await FenceAsync(run);
        return hold;
    }

    /// <summary>
    /// Releases the hold and waits for a tail event written after it: the pump has dispatched through the drop. The tail is
    /// written once the last filler is in dispatch, so the buffer has room for it and it is not refused in its turn.
    /// </summary>
    private static async Task DeliverAsync(Run run, BootingAsterisk peer, Hold hold, string name)
    {
        var fills = 0;
        var lastFill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = run.Connection.Subscribe(new Observer(e =>
        {
            if (e is not UserEventEvent u) return;
            if (u.UserEvent == name + "-fill" && Interlocked.Increment(ref fills) == Capacity)
                lastFill.TrySetResult();
            else if (u.UserEvent == name + "-tail")
                tail.TrySetResult();
            else if (u.UserEvent == name + "-after-tail")
                afterTail.TrySetResult();
        }));
        hold.Release();
        await lastFill.Task.WaitAsync(Run.Bound);
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame(name + "-tail"));
        await tail.Task.WaitAsync(Run.Bound);

        // The tail's observers run before its dispatch has returned, and the pump passes the drop only once it has. The
        // pump reads the next event only after that, so the next event's dispatch says the tail's has been counted.
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame(name + "-after-tail"));
        await afterTail.Task.WaitAsync(Run.Bound);
    }

    // Internal to the connection and not part of what Live binds to, so it is read the way the reconnect tests read it.
    private static async Task PendingNotificationsAsync(Run run)
    {
        var pending = (Task)typeof(AmiConnection)
            .GetProperty("PendingNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(run.Connection)!;
        await pending.WaitAsync(Run.Bound);
    }

    private sealed class Hold : IDisposable
    {
        private readonly IDisposable _subscription;

        public Hold(AmiConnection connection, string name)
        {
            Gate = new DispatchGate(name);
            _subscription = connection.Subscribe(Gate);
        }

        public DispatchGate Gate { get; }

        public void Release() => Gate.Release();

        public void Dispose()
        {
            Gate.Release();
            _subscription.Dispose();
            Gate.Dispose();
        }
    }

    private sealed class Observer(Action<ManagerEvent> next) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => next(value);

        public void OnError(Exception error)
        {
            // The connection never faults its observers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to do.
        }
    }

    /// <summary>The <c>live channel-reconcile</c> spans, attributed by the parent activity each traced call runs in.</summary>
    private sealed class Spans : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;

        public Spans()
        {
            // The source exists before the listener is added, so the listener is offered it: one created inside
            // ShouldListenTo would miss the listener being added.
            var testSource = TestSource;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is "Verbara.Sdk.Live" || source == testSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        /// <summary>Runs <paramref name="read"/> under a parent of its own and returns its one reconciliation span.</summary>
        public async Task<Activity> TraceAsync(Func<Task> read, bool expectThrow = false)
        {
            string parentId;
            Task task;
            using (var parent = TestSource.StartActivity("channel-read-coverage")
                ?? throw new InvalidOperationException("The test's parent activity was not sampled."))
            {
                parentId = parent.Id!;
                task = read();
            }

            var thrown = await Record.ExceptionAsync(() => task.WaitAsync(Run.Bound));
            if (!expectThrow && thrown is not null)
                throw new InvalidOperationException("The traced read threw.", thrown);

            return _stopped.Single(a => a.ParentId == parentId && a.OperationName == ReconcileSpan);
        }

        public void Dispose() => _listener.Dispose();
    }
}
