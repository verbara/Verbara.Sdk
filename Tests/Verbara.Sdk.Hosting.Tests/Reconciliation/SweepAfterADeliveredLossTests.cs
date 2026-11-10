using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A young call whose last <c>Hangup</c> the full event buffer dropped is verified at the first sweep tick after the
/// connection has dispatched through the drop, instead of once it is older than the dialing timeout; no tick sends a
/// <c>Status</c> while the drop is still behind the backlog, and only a reconciliation that covers the loss clears it.
/// The sweep says what prompted each verification (<c>sessions.trigger</c>: <c>age</c>, <c>table</c>,
/// <c>event-loss</c>, or <c>none</c>).
/// </summary>
/// <remarks>
/// Over <see cref="LossRig"/>: a real connection with a two-event buffer, a peer that plays Asterisk truthfully, the
/// sweep run one tick at a time, single-server and pool, on the manual clocks. Every call here is younger than the
/// dialing timeout unless a test ages it.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
public sealed class SweepAfterADeliveredLossTests
{
    private const string EventLoss = "event-loss";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyOnceAndEndTheGhost_WhenADroppedHangupHasBeenDeliveredThrough(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("G", "G");
        await rig.NewChannelAsync("K", "K");
        var ghost = rig.Manager.GetByLinkedId("G")!;
        var kept = rig.Manager.GetByLinkedId("K")!;
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        await rig.DeliverAsync();

        var verified = await rig.TickAsync(pool);
        var after = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            verified.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
                "the drop is delivered through and the ghost's call holds a channel the table holds: the first tick verifies " +
                $"once, prompted by the loss, whatever the calls' age. {rig.Describe(ghost)}");
            rig.EndingsOf(ghost).Should().Be(1, "the snapshot no longer lists the ghost's channel");
            ghost.Metadata.GetValueOrDefault("cause").Should().Be("reload", "the ghost ends through the unobserved-ending path");
            rig.EndingsOf(kept).Should().Be(0, "the other young call is untouched");
            after.Should().BeEquivalentTo(new { Status = 0, Verification = "skipped:no-candidate", Trigger = "none" },
                "the covering verification cleared the loss and no call is old enough");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldSendNothingAndNotWait_WhileTheLossIsBehindTheBacklog_ThenVerifyOnceItIsDelivered(bool pool)
    {
        // The table is not complete (the first load's Status was refused), so a call not observed in full waits for it:
        // today's waiting-call source would verify at once, into the backlog.
        await using var rig = await LossRig.StartAsync(refuseFirstLoad: true);
        await rig.NewChannelAsync("G", "G");
        await rig.NewChannelAsync("K", "K");
        var waiting = LossRig.Reconstructed("R");
        rig.Manager.RegisterReconstructedSession(waiting).Should().BeTrue();
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");

        var behind = await rig.TickAsync(pool);
        await rig.DeliverAsync();
        var delivered = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            behind.Should().BeEquivalentTo(new { Status = 0, Waited = false, Verification = "skipped:no-candidate" },
                "a loss whose last refused event the connection has not dispatched through adds no candidate and withholds " +
                "the waiting-call source; the tick returns without waiting for the backlog");
            behind.Line.Should().Contain("loss=pending-undelivered", "the result line says a loss waits on its backlog");
            delivered.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = "table" },
                "the first tick after delivery verifies once; a call waiting for an incomplete table outranks the loss");
            rig.EndingsOf(waiting).Should().Be(1, "the snapshot lists no channel of the waiting call: it ends once");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyAgain_WhenAnEventWasDroppedAfterTheLastReadsStatusWasSent(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("K", "K");
        await rig.NewChannelAsync("H", "H");
        var late = rig.Manager.GetByLinkedId("H")!;
        rig.AnswerNextStatusByHand();
        var read = rig.Server.ReconcileChannelsAsync().AsTask();
        var id = await rig.StatusIdAsync(1);
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("H");
        await rig.DeliverAsync();
        await rig.AnswerStatusAsync(id, "K", "H");
        await read.WaitAsync(LossRig.Bound);

        var again = await rig.TickAsync(pool);
        var settled = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            again.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
                $"the read completed in full, but H's Hangup was dropped after its Status was sent: the loss stays pending. {rig.Describe(late)}");
            rig.EndingsOf(late).Should().Be(1, "the second snapshot omits H");
            settled.Status.Should().Be(0, "a verification with no drop after its Status covered the loss");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyAgain_WhenTheLastReadKeptAChannelWhoseNewchannelWasStillBuffered(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("K", "K");
        await rig.HoldAsync();
        await rig.BufferNewChannelAsync("N", "N");
        await rig.BufferAsync("PeerStatus", [new("Peer", "FILL/N"), new("PeerStatus", "Reachable")], "FILL/N");
        await rig.DropHangupAsync("N");

        // The read's Status is sent while N's Newchannel waits in the buffer: N is admitted above its window and kept.
        rig.AnswerNextStatusByHand();
        var read = rig.Server.ReconcileChannelsAsync().AsTask();
        var id = await rig.StatusIdAsync(1);
        await rig.AnswerStatusAsync(id, "K");
        await rig.DeliverAsync();
        await read.WaitAsync(LossRig.Bound);
        var call = rig.Manager.GetByLinkedId("N")!;

        var again = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            again.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
                $"a read that kept a channel whose creation it received before its send covers nothing. {rig.Describe(call)}");
            rig.EndingsOf(call).Should().Be(1, "the next snapshot omits N, so its call ends");
            call.Metadata.GetValueOrDefault("cause").Should().Be("reload");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyAgain_WhenTheOnlyReadsAfterTheDeliveredLossOverlapped(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("G", "G");
        await rig.NewChannelAsync("K", "K");
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        await rig.DeliverAsync();
        rig.AnswerNextStatusByHand();
        rig.AnswerNextStatusByHand();
        var first = rig.Server.ReconcileChannelsAsync().AsTask();
        var firstId = await rig.StatusIdAsync(1);
        var second = rig.Server.ReconcileChannelsAsync().AsTask();
        var secondId = await rig.StatusIdAsync(2);
        await rig.AnswerStatusAsync(secondId, "K");
        await rig.AnswerStatusAsync(firstId, "K");
        await Task.WhenAll(first, second).WaitAsync(LossRig.Bound);

        var again = await rig.TickAsync(pool);

        again.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
            "two channel reads in flight at once overlap, and an overlapped read covers no loss");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyAgain_WhenTheOnlyReadAfterTheDeliveredLossRanInsideAnEventHandler(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("G", "G");
        await rig.NewChannelAsync("K", "K");
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        await rig.DeliverAsync();
        await rig.InHandlerAsync(() => rig.Server.ReconcileChannelsAsync().AsTask());

        var again = await rig.TickAsync(pool);

        again.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
            "a read run inside an event handler cannot wait for the events received before its snapshot: it covers nothing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyAgain_WhenTheLastReadsWaitRanOut(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("K", "K");
        await rig.NewChannelAsync("G", "G");
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        var deferred = await rig.RunAsync(() => rig.Server.ReconcileChannelsAsync().AsTask());
        await rig.DeliverAsync();

        var again = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            deferred.Should().BeTrue("premise: the read's barrier waited on the held dispatch and ran out");
            again.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
                "a read whose wait ran out removed nothing and covers nothing; once the loss is delivered the sweep verifies");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldSkipThenVerify_WhenALoadWasInFlightAtTheTickAfterTheDeliveredLoss(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("G", "G");
        await rig.NewChannelAsync("K", "K");
        var ghost = rig.Manager.GetByLinkedId("G")!;
        rig.AnswerNextStatusByHand();
        var load = rig.Server.RequestInitialStateAsync().AsTask();
        var loadId = await rig.StatusIdAsync(1);
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        await rig.DeliverAsync();

        var skipped = await rig.TickAsync(pool);
        await rig.AnswerStatusAsync(loadId, "G", "K");
        await load.WaitAsync(LossRig.Bound);
        var verified = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            skipped.Should().BeEquivalentTo(new { Status = 0, Verification = "skipped:load-in-flight", Trigger = EventLoss },
                "the delivered loss makes the ghost's call a candidate, and the load in flight makes the tick skip");
            verified.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
                $"the load sent its Status before the drop, so it did not cover it; a skip leaves the loss pending. {rig.Describe(ghost)}");
            rig.EndingsOf(ghost).Should().Be(1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldSendNothing_WhenTheReloadAfterAReconnectCoveredTheLoss(bool pool)
    {
        await using var rig = await LossRig.StartAsync(autoReconnect: true);
        await rig.NewChannelAsync("K", "K");
        await rig.NewChannelAsync("G", "G");
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        await rig.DeliverAsync();
        rig.CloseSession();
        await rig.LoggedInAsync(2);
        await rig.ReconnectHandledAsync();
        var asked = rig.StatusAsked;

        var tick = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            asked.Should().Be(1, "premise: the reload sent one Status and it completed in full");
            tick.Status.Should().Be(0, "the reload after the reconnect is a covering read: no loss is pending");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyEveryTick_WhileItsStatusIsRefused_AndStopOnceOneCovers(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("G", "G");
        await rig.NewChannelAsync("K", "K");
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        await rig.DeliverAsync();
        rig.RefuseNextStatus();
        rig.RefuseNextStatus();

        var first = await rig.TickAsync(pool);
        var second = await rig.TickAsync(pool);
        var covering = await rig.TickAsync(pool);
        var after = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            first.Should().BeEquivalentTo(new { Status = 1, Trigger = EventLoss }, "the delivered loss prompts one Status");
            second.Should().BeEquivalentTo(new { Status = 1, Trigger = EventLoss },
                "a refused Status covers nothing: the loss stays pending and the server a candidate, one Status per tick");
            covering.Should().BeEquivalentTo(new { Status = 1, Trigger = EventLoss }, "the third Status completes");
            after.Status.Should().Be(0, "the completed Status covered the loss");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldSendNothingAndReportTheLossUndelivered_WhileTheReconnectIsPending(bool pool)
    {
        await using var rig = await LossRig.StartAsync(autoReconnect: true);
        await rig.NewChannelAsync("K", "K");
        await rig.HoldAsync();
        await rig.BufferAsync("PeerStatus", [new("Peer", "FILL/reconnect"), new("PeerStatus", "Reachable")], "FILL/reconnect");
        await rig.FenceAsync();
        rig.CloseSession();
        await rig.LoggedInAsync(2);
        var pending = rig.Server.IsReconnectPending;

        var window = await rig.TickAsync(pool);
        await rig.DeliverAsync();
        await rig.ReconnectHandledAsync();
        var after = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            pending.Should().BeTrue("premise: the lost session's delivery is held by its handler while the new session is logged in");
            window.Status.Should().Be(0, "nothing is sent while the lost session's backlog is in delivery");
            window.Line.Should().Contain("loss=pending-undelivered",
                "the session change is a loss pending and undelivered while the reconnect is pending");
            after.Status.Should().Be(0, "the reload's read covered the session change");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldSendNothingAndCountNothing_WhenTheDeliveredLossFindsNoCallHoldingAChannel(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();
        await rig.DeliverAsync();

        var tick = await rig.TickAsync(pool);

        tick.Should().BeEquivalentTo(new { Status = 0, Unverifiable = 0, Verification = "skipped:no-candidate" },
            "a pending, delivered loss with no call holding a channel has nothing a snapshot could prove gone");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldSendNothing_WhenOnlyCopiesOfACollectedAnswerWereDropped(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("K", "K");
        await rig.NewChannelAsync("J", "J");
        await rig.HoldAndFillAsync();
        var read = await rig.RunAsync(() => rig.Server.ReconcileChannelsAsync().AsTask());
        await rig.DeliverAsync();
        await rig.ReconcileAsync();

        var tick = await rig.TickAsync(pool);

        using (new AssertionScope())
        {
            read.Should().BeTrue("premise: the read's answer copies met the full buffer");
            tick.Status.Should().Be(0, "a refused copy of an answer the action received is no loss");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldVerifyOnce_WhenADroppedAnswerCopyIsReadAsAStateChange(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("K", "K");
        await rig.HoldAndFillAsync();
        var list = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new PresenceStateListAction()));
        var id = await rig.PresenceListAsked.WaitAsync(LossRig.Bound);
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        for (var i = 0; i < 2; i++)
            await rig.Peer.WriteEventAsync("DeviceStateChange", [new("ActionID", id), new("Device", $"Custom:d-{i}"), new("State", "INUSE")]);
        await rig.Peer.WriteEventAsync("DeviceStateListComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "2")]);
        await rig.FenceAsync();
        await list.WaitAsync(LossRig.Bound);

        await rig.DeliverAsync();
        var tick = await rig.TickAsync(pool);

        tick.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = EventLoss },
            "an observer cannot tell a DeviceStateChange copy from a state change, so its refusal is a loss");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldReportTheAgeFirst_WhenAnOldCallAndADeliveredLossBothPromptTheVerification(bool pool)
    {
        await using var rig = await LossRig.StartAsync();
        await rig.NewChannelAsync("G", "G");
        await rig.NewChannelAsync("O", "O");
        SweepRig.CreatedAtField.SetValue(rig.Manager.GetByLinkedId("O")!, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        await rig.HoldAndFillAsync();
        await rig.DropHangupAsync("G");
        await rig.DeliverAsync();

        var tick = await rig.TickAsync(pool);

        tick.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = "age" },
            "one verification serves both; the age is reported first");
    }

    private static async Task ReadAllAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        await foreach (var _ in events)
        {
            // Read to its end.
        }
    }
}
