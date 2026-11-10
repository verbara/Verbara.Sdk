using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A call the session engine has not observed in full is decided only on a channel table no dropped event has left
/// untrusted: once the full buffer has refused an event no action received, such a call is kept — no leg retired, no
/// ending, no promotion — until a channel reconciliation covers the loss. No part of the engine sends a <c>Status</c>
/// for it while the loss is behind the backlog, a reconnect's lost-session backlog included; once the loss is
/// delivered, the sweep's first verification, an admission on a complete table, or the host's own reconciliation
/// covers it, and that read's completion decides the call.
/// </summary>
/// <remarks>
/// Over <see cref="LossRig"/>: a real connection with a two-event buffer, a <see cref="CallSessionManager"/> attached to
/// the server, one sweep tick at a time on the manual clocks. The drop is a <c>Newchannel</c> (or another event no
/// action received) refused while a handler holds the dispatch with the buffer full; it is delivered once a tail event
/// written after the release has been dispatched.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
public sealed class LossAwareTableDecisionTests
{
    private const string Reload = "reload";

    private static SessionParticipant Leg(CallSession call, string uid) => call.Participants.Single(p => p.UniqueId == uid);

    private static bool Left(CallSession call, string uid) => Leg(call, uid).LeftAt is not null;

    // ---- (a) the registration ----

    [Theory]
    [InlineData("X,Y")]
    [InlineData("X")]
    [InlineData("")]
    public async Task RegisterReconstructedSession_ShouldKeepTheCallUntilACoveringRead_WhenALegsNewchannelWasDropped(string listed)
    {
        await using var rig = await LossRig.StartAsync(attach: false, listed: ("X", "L"));
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropNewChannelAsync("Y", "L");
        var call = LossRig.Reconstructed("L", "X", "Y");
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var atRegistration = (YLeft: Left(call, "Y"), call.Unverified, Endings: rig.EndingsOf(call), Reloads: ReloadDepartures(call));

        var behind = await rig.TickAsync(pool: false);
        await rig.DeliverAsync();
        rig.NextStatusLists(Split(listed));
        var covering = await rig.TickAsync(pool: false);
        var atCovering = (YLeft: Left(call, "Y"), call.Unverified, Endings: rig.EndingsOf(call), Reloads: ReloadDepartures(call));
        if (listed == "X,Y")
        {
            await rig.HangUpAsync("X");
            await rig.HangUpAsync("Y");
        }
        else if (listed == "X")
        {
            await rig.HangUpAsync("X");
        }

        using (new AssertionScope())
        {
            atRegistration.Should().Be((false, true, 0, 0),
                "Y's Newchannel was dropped, so the table is not covered: Y is not retired, the call is kept not observed in full. " + rig.Describe(call));
            behind.Should().BeEquivalentTo(new { Status = 0, Verification = "skipped:no-candidate" },
                "nothing is sent while the loss is behind the backlog");
            covering.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = "event-loss" },
                "the first tick after delivery sends one Status, prompted by the loss");
            switch (listed)
            {
                case "X,Y":
                    atCovering.Should().Be((false, false, 0, 0), "the covering snapshot lists both legs: the call is judged observed with both");
                    rig.EndingsOf(call).Should().Be(1, "it ends once, at its last leg's hangup");
                    call.HangupCause.Should().Be(HangupCause.NormalClearing);
                    call.Metadata.GetValueOrDefault("cause").Should().BeNull();
                    break;
                case "X":
                    atCovering.Should().Be((true, false, 0, 1), "the covering snapshot omits Y: Y is retired then, with reload provenance, and the call kept for X");
                    rig.EndingsOf(call).Should().Be(1, "it ends once, at X's hangup, with X's cause");
                    call.HangupCause.Should().Be(HangupCause.NormalClearing);
                    break;
                default:
                    atCovering.Endings.Should().Be(1, "the covering snapshot lists neither leg: the call ends once");
                    call.Metadata.GetValueOrDefault("cause").Should().Be(Reload);
                    break;
            }
        }
    }

    // ---- (b) the waiting-call source ----

    [Fact]
    public async Task Sweep_ShouldSendNothingForACallWaitingOnAnIncompleteTable_WhileTheLossIsBehindTheBacklog()
    {
        await using var rig = await LossRig.StartAsync(refuseFirstLoad: true);
        var waiting = LossRig.Reconstructed("R");
        rig.Manager.RegisterReconstructedSession(waiting).Should().BeTrue();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();

        var first = await rig.TickAsync(pool: true);
        var second = await rig.TickAsync(pool: true);
        await rig.DeliverAsync();
        var delivered = await rig.TickAsync(pool: true);

        using (new AssertionScope())
        {
            first.Should().BeEquivalentTo(new { Status = 0, Waited = false, Verification = "skipped:no-candidate" },
                "a call waiting on an incomplete table adds no candidate while a loss is behind the backlog");
            second.Should().BeEquivalentTo(new { Status = 0, Waited = false, Verification = "skipped:no-candidate" },
                "nor on the next tick");
            delivered.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = "table" },
                "the first tick after delivery verifies once, prompted by the table");
            rig.EndingsOf(waiting).Should().Be(1, "the snapshot lists nothing of R");
            waiting.Metadata.GetValueOrDefault("cause").Should().Be(Reload);
        }
    }

    // ---- (c) the admission during an undelivered loss on an incomplete table ----

    [Fact]
    public async Task AdmitExistingCalls_ShouldSendNoStatus_WhileTheLossIsBehindTheBacklogOnAnIncompleteTable()
    {
        await using var rig = await LossRig.StartAsync(attach: false, refuseFirstLoad: true);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();

        var asked = rig.StatusAsked;
        var waited = await rig.AdmitAsync();
        var sentByAdmission = rig.StatusAsked - asked;
        var call = rig.Manager.GetByLinkedId("L")!;
        var afterAdmission = call.Unverified;
        await rig.DeliverAsync();
        var tick = await rig.TickAsync(pool: false);

        using (new AssertionScope())
        {
            sentByAdmission.Should().Be(0, "no Status is requested while the loss is behind the backlog");
            waited.Should().BeFalse("the admission does not wait on the backlog");
            afterAdmission.Should().BeTrue("the walk admits, and the calls wait not observed in full");
            tick.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = "table" },
                "the first tick after delivery sends one Status");
            call.Unverified.Should().BeFalse("its completion decides the call: observed with X and Y present. " + rig.Describe(call));
            rig.EndingsOf(call).Should().Be(0);
        }
    }

    // ---- (d) a legless call not observed in full on a complete table, loss delivered ----

    [Fact]
    public async Task Sweep_ShouldVerifyALeglessCallNotObservedInFull_WhenADeliveredLossLeftTheCompleteTableUncovered()
    {
        await using var rig = await LossRig.StartAsync();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();
        await rig.DeliverAsync();
        var call = LossRig.Reconstructed("R");
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var atRegistration = rig.EndingsOf(call);

        var tick = await rig.TickAsync(pool: false);

        using (new AssertionScope())
        {
            atRegistration.Should().Be(0, "a table a delivered loss left uncovered decides nothing");
            tick.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = "event-loss", Unverifiable = 0 },
                "a call not observed in full is a candidate once the loss is delivered, whatever its channels");
            rig.EndingsOf(call).Should().Be(1, "the covering snapshot lists nothing of its linkedid");
            call.Metadata.GetValueOrDefault("cause").Should().Be(Reload);
        }
    }

    // ---- (f) the hangup received before the drop ----

    [Theory]
    [InlineData("Y")]
    [InlineData("")]
    public async Task OnChannelRemoved_ShouldKeepTheCall_WhenTheHangupWasReceivedBeforeTheDropAndDeliveredAfterIt(string listed)
    {
        using var front = new FrontHold(e => e.EventType == "Hangup" && e.RawFields?.GetValueOrDefault("Uniqueid") == "X");
        await using var rig = await LossRig.StartAsync(attach: false, beforeServer: front, listed: ("X", "L"));
        rig.Attach();
        await rig.WriteHangupAsync("X");
        await front.Held.WaitAsync(LossRig.Bound);
        await rig.FillAsync();
        await rig.DropNewChannelAsync("Y", "L");
        var call = LossRig.Reconstructed("L", "X", "Y");
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var yLeftAtRegistration = Left(call, "Y");

        front.Release();
        await rig.DispatchedAsync("Hangup", "X");
        var afterTheHangup = (YLeft: Left(call, "Y"), Endings: rig.EndingsOf(call));
        await rig.DeliverAsync();
        rig.NextStatusLists(Split(listed));
        var span = await rig.ReconcileAsync();
        var afterTheRead = (call.Unverified, Endings: rig.EndingsOf(call));
        if (listed == "Y")
            await rig.HangUpAsync("Y");

        using (new AssertionScope())
        {
            yLeftAtRegistration.Should().BeFalse("Y is not retired at the registration while its Newchannel's drop is pending");
            afterTheHangup.Should().Be((false, 0),
                "X's Hangup, received before the drop, decides inside its own frame: the current pump's loss is pending, so the " +
                "call stays in progress with Y present. " + rig.Describe(call));
            span.GetValueOrDefault("live.reconcile.covered").Should().Be("True", "the host's own read outside a dispatch covers the loss");
            if (listed == "Y")
            {
                afterTheRead.Should().Be((false, 0), "the covering snapshot lists Y: the call is kept with Y, observed");
                rig.EndingsOf(call).Should().Be(1, "it ends once, at Y's hangup");
                call.HangupCause.Should().Be(HangupCause.NormalClearing);
            }
            else
            {
                afterTheRead.Endings.Should().Be(1, "the covering snapshot lists nothing of L: the call ends once");
                call.Metadata.GetValueOrDefault("cause").Should().Be(Reload,
                    "Y was never held by the table nor found being removed, so its retirement is the last departure the call knows of");
                call.HangupCause.Should().BeNull("X's recorded hangup does not supply the cause of an ending the engine did not observe");
                ReloadDepartures(call).Should().Be(1, "exactly one ParticipantLeft(reload), Y's");
                call.Events.Should().Contain(e => e.Type == CallSessionEventType.ParticipantLeft
                    && e.SourceChannel == Leg(call, "X").Channel && e.Detail == HangupCause.NormalClearing.ToString(),
                    "X's own departure keeps the cause that was observed");
            }
        }
    }

    [Fact]
    public async Task ChannelSnapshotCompleted_ShouldKeepTheCall_WhenTheHangupsOwnHandlerReconcilesInsideItsFrame()
    {
        using var front = new FrontHold(e => e.EventType == "Hangup" && e.RawFields?.GetValueOrDefault("Uniqueid") == "X");
        await using var rig = await LossRig.StartAsync(attach: false, beforeServer: front, listed: ("X", "L"));
        rig.Attach();
        var inHandler = rig.InHandlerOf("X", () => rig.Server.ReconcileChannelsAsync().AsTask());
        await rig.WriteHangupAsync("X");
        await front.Held.WaitAsync(LossRig.Bound);
        await rig.FillAsync();
        await rig.DropNewChannelAsync("Y", "L");
        var call = LossRig.Reconstructed("L", "X", "Y");
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.NextStatusLists();

        front.Release();
        await inHandler.WaitAsync(LossRig.Bound);

        using (new AssertionScope())
        {
            rig.EndingsOf(call).Should().Be(0,
                "the read run inside X's Hangup handler skipped its barrier and covers nothing, so its completion decides " +
                "nothing: the call is still in progress. " + rig.Describe(call));
            call.State.Should().NotBe(CallSessionState.Completed);
        }
    }

    // ---- (g1) the admission with no sweep ----

    [Fact]
    public async Task AdmitExistingCalls_ShouldSendOneCoveringStatus_WhenADeliveredLossLeftTheCompleteTableUncovered()
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();
        await rig.DeliverAsync();

        var asked = rig.StatusAsked;
        await rig.AdmitAsync();
        var sent = rig.StatusAsked - asked;
        var call = rig.Manager.GetByLinkedId("L")!;
        var observed = !call.Unverified;
        var tick = await rig.TickAsync(pool: false);

        using (new AssertionScope())
        {
            sent.Should().Be(1, "the admission requests one covering read on a complete table a delivered loss left uncovered");
            observed.Should().BeTrue("the admission's close decides the call on the table that snapshot covered. " + rig.Describe(call));
            call.Participants.Where(p => p.LeftAt is null).Select(p => p.UniqueId).Should().BeEquivalentTo(["X", "Y"]);
            tick.Should().BeEquivalentTo(new { Status = 0, Verification = "skipped:no-candidate" }, "the loss is covered");
        }
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldLeaveTheCallNotObservedInFull_WhenAnEventIsDroppedBetweenItsSnapshotAndItsClose()
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();
        await rig.DeliverAsync();

        Task<bool> admission;
        using (var parked = StepParker.Install(rig.Manager, HandlerStep.AdmissionClosing, LossRig.ServerId))
        {
            admission = rig.AdmitAsync();
            await Task.Run(() => parked.WaitUntilParked());
            await rig.HoldAndFillAsync();
            await rig.DropNewChannelAsync("Z", "L");
            parked.LetGo();
            await admission.WaitAsync(LossRig.Bound);
        }

        var call = rig.Manager.GetByLinkedId("L")!;
        call.Unverified.Should().BeTrue(
            "a drop between the admission's snapshot and its close leaves the table uncovered at the close: the admitted call " +
            "waits not observed in full. " + rig.Describe(call));
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldSendNothingAndDecideNothing_WhileTheLossIsBehindTheBacklogOnACompleteTable()
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();

        var asked = rig.StatusAsked;
        await rig.AdmitAsync();
        var call = rig.Manager.GetByLinkedId("L")!;

        using (new AssertionScope())
        {
            (rig.StatusAsked - asked).Should().Be(0, "nothing is requested while the loss is behind the backlog");
            call.Unverified.Should().BeTrue("the calls wait not observed in full for a covering read. " + rig.Describe(call));
        }
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldNotEndTheCallAtItsLastHangup_WhileTheLossIsBehindTheBacklog_ThenTheHostsReadEndsItOnce()
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();

        CallSession call;
        int endedAtTheClose;
        bool unverifiedAtTheClose;
        using (var parked = StepParker.Install(rig.Manager, HandlerStep.AdmissionClosing, LossRig.ServerId))
        {
            var admission = rig.AdmitAsync();
            await Task.Run(() => parked.WaitUntilParked());
            call = rig.Manager.GetByLinkedId("L")!;
            // The dispatch is held, so the hangups reach Live through its own entry point, the path a Hangup takes.
            rig.ForgetChannel("X");
            rig.ForgetChannel("Y");
            await Task.Run(() => rig.Server.Channels.OnHangup("X", HangupCause.NormalClearing));
            await Task.Run(() => rig.Server.Channels.OnHangup("Y", HangupCause.NormalClearing));
            parked.LetGo();
            await admission.WaitAsync(LossRig.Bound);
            endedAtTheClose = rig.EndingsOf(call);
            unverifiedAtTheClose = call.Unverified;
        }

        await rig.DeliverAsync();
        var span = await rig.ReconcileAsync();

        using (new AssertionScope())
        {
            endedAtTheClose.Should().Be(0,
                "the table is not covered while the loss is behind the backlog: the call is not ended at its last leg's hangup");
            unverifiedAtTheClose.Should().BeTrue("the call is still not observed in full at the admission's close");
            span.GetValueOrDefault("live.reconcile.covered").Should().Be("True", "the host's read after delivery covers the loss");
            rig.EndingsOf(call).Should().Be(1, "its completion ends the call exactly once. " + rig.Describe(call));
        }
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldCoverNothing_WhenAwaitedFromInsideAnEventHandler()
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();
        await rig.DeliverAsync();

        var asked = rig.StatusAsked;
        var mark = rig.SpanMark;
        await rig.InHandlerAsync(() => rig.Manager.AdmitExistingCalls(LossRig.ServerId).AsTask());
        var sent = rig.StatusAsked - asked;
        var spans = rig.ReconcileSpansSince(mark);
        var call = rig.Manager.GetByLinkedId("L")!;
        var unverified = call.Unverified;
        var host = await rig.ReconcileAsync();

        using (new AssertionScope())
        {
            sent.Should().Be(1, "the admission on a complete, uncovered table sends its Status");
            spans.Should().ContainSingle().Which.Should().Contain(new KeyValuePair<string, string?>("live.reconcile.covered", "False"),
                "awaited from inside a handler its barrier is skipped, so it covers nothing");
            spans.SingleOrDefault()?.Keys.Should().NotContain(["live.reconcile.deferred", "live.reconcile.kept_stale",
                "live.reconcile.overlapped", "live.status.refused"], "a read skipped in dispatch carries no other reason");
            unverified.Should().BeTrue("the admitted calls stay not observed in full");
            host.GetValueOrDefault("live.reconcile.covered").Should().Be("True");
            call.Unverified.Should().BeFalse("the host's covering read decides them. " + rig.Describe(call));
        }
    }

    // ---- (g2) the host's own read ----

    [Theory]
    [InlineData("Y")]
    [InlineData("")]
    public async Task ReconcileChannelsAsync_ShouldDecideTheCallTheDropLeftWaiting_WhenNoSweepRuns(string listed)
    {
        await using var rig = await LossRig.StartAsync(attach: false, listed: ("X", "L"));
        rig.Attach();
        await rig.HoldAndFillAsync();
        await rig.DropNewChannelAsync("Y", "L");
        await rig.DeliverAsync();
        var call = LossRig.Reconstructed("L", "X", "Y");
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();

        await rig.HangUpAsync("X");
        var afterX = (YLeft: Left(call, "Y"), Endings: rig.EndingsOf(call));
        rig.NextStatusLists(Split(listed));
        var span = await rig.ReconcileAsync();
        var afterTheRead = (call.Unverified, Endings: rig.EndingsOf(call));
        if (listed == "Y")
            await rig.HangUpAsync("Y");

        using (new AssertionScope())
        {
            afterX.Should().Be((false, 0), "X's hangup on an uncovered table leaves the call in progress with Y present. " + rig.Describe(call));
            span.GetValueOrDefault("live.reconcile.covered").Should().Be("True");
            if (listed == "Y")
            {
                afterTheRead.Should().Be((false, 0), "kept with Y and judged observed");
                rig.EndingsOf(call).Should().Be(1, "ended once at Y's hangup, with Y's cause");
                call.HangupCause.Should().Be(HangupCause.NormalClearing);
                call.Metadata.GetValueOrDefault("cause").Should().BeNull();
            }
            else
            {
                afterTheRead.Endings.Should().Be(1, "the snapshot lists nothing of L");
                call.Metadata.GetValueOrDefault("cause").Should().Be(Reload,
                    "Y was never held by the table nor found being removed, so its retirement is the last departure the call knows of");
                call.HangupCause.Should().BeNull("X's recorded hangup does not supply the cause of an ending the engine did not observe");
                ReloadDepartures(call).Should().Be(1, "exactly one ParticipantLeft(reload), Y's");
                call.Events.Should().Contain(e => e.Type == CallSessionEventType.ParticipantLeft
                    && e.SourceChannel == Leg(call, "X").Channel && e.Detail == HangupCause.NormalClearing.ToString(),
                    "X's own departure keeps the cause that was observed");
            }
        }
    }

    // ---- (h) the reconnect window ----

    [Fact]
    public async Task AdmitExistingCalls_ShouldSendNoStatus_WhileTheConnectionsReconnectIsPending()
    {
        await using var rig = await LossRig.StartAsync(attach: false, autoReconnect: true);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAsync();
        await rig.BufferAsync("PeerStatus", [new("Peer", "FILL/lost"), new("PeerStatus", "Reachable")], "FILL/lost");
        await rig.FenceAsync();
        rig.CloseSession();
        await rig.LoggedInAsync(2);
        var pending = rig.Server.IsReconnectPending;

        var asked = rig.StatusAsked;
        await rig.AdmitAsync();
        var sent = rig.StatusAsked - asked;
        var call = rig.Manager.GetByLinkedId("L")!;
        var unverified = call.Unverified;
        var tick = await rig.TickAsync(pool: false);
        // Released, not delivered by a tail: a Status sent into the new session's buffer may fill it, and the reload's
        // return is the signal that the lost session's backlog was dispatched.
        rig.Release();
        await rig.ReconnectHandledAsync();

        using (new AssertionScope())
        {
            pending.Should().BeTrue("premise: the lost session's delivery is held by its handler");
            sent.Should().Be(0, "a session change is a loss pending and undelivered while the reconnect is pending");
            unverified.Should().BeTrue("the walk admits, and the calls wait not observed in full");
            tick.Status.Should().Be(0);
            tick.Line.Should().Contain("loss=pending-undelivered");
            call.Unverified.Should().BeFalse("the reload's covering read decides them, with X and Y present. " + rig.Describe(call));
            rig.EndingsOf(call).Should().Be(0);
        }
    }

    private static int ReloadDepartures(CallSession call) =>
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == Reload);

    private static string[] Split(string listed) =>
        listed.Length == 0 ? [] : listed.Split(',');
}
