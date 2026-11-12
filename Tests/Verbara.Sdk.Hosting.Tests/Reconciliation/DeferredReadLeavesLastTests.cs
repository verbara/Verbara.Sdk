using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// Two channel reads of one server apply at the same time: B, whose barrier ran out (it applies deferred and writes the
/// verified flag down), and A, whose wait was met (it writes the flag up). When B writes first and A then writes, leaves
/// and finds B still counted, the table reads covered only once B leaves, and nothing else will announce it: no
/// reconnect ends, and a later admission returns at once on a covered table. B, leaving last, must announce it, so that
/// a call waiting on a covered table is decided with no sweep tick and no further channel read.
/// </summary>
/// <remarks>
/// Over <see cref="LossRig"/>. The order is forced, not timed: B is held at
/// <see cref="VerbaraServer.ChannelApplicationWritten"/> after its flag write, and, when a delivered loss is pending, A
/// is held inside its own application (on <c>ChannelAdded</c> for a channel only its read lists) until B has written,
/// so A begins to apply before B is counted and still covers the loss. No wait ends on the clock: B's barrier is run out
/// on the rig's <see cref="LossRig.LiveClock"/>, and every other wait ends on the step it waits for.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
public sealed class DeferredReadLeavesLastTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_ShouldVerifyTheWaitingCall_WhenAReadThatWaitedOutItsBoundLeavesAfterTheReadThatVerifiedTheTable(
        bool deliveredLoss)
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        var run = await RunAsync(rig, deliveredLoss, legsGone: false);

        using (new AssertionScope())
        {
            run.AssertPremises(deliveredLoss);
            run.Call.Unverified.Should().BeFalse(
                "the table became covered when B left: the waiting call is verified, with no sweep. " + rig.Describe(run.Call));
            rig.EndingsOf(run.Call).Should().Be(0);
            rig.StatusAsked.Should().Be(run.StatusAskedAfterB, "no further channel read took part");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_ShouldEndTheWaitingCall_WhenItsLegsAreGoneAndAReadThatWaitedOutItsBoundLeavesAfterTheReadThatVerifiedTheTable(
        bool deliveredLoss)
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        var run = await RunAsync(rig, deliveredLoss, legsGone: true);

        using (new AssertionScope())
        {
            run.AssertPremises(deliveredLoss);
            rig.EndingsOf(run.Call).Should().Be(1,
                "the table became covered when B left: the call whose legs are gone ends, with no sweep. " + rig.Describe(run.Call));
            run.Call.Metadata.GetValueOrDefault("cause").Should().Be("reload");
            run.Call.HangupCause.Should().BeNull();
            rig.StatusAsked.Should().Be(run.StatusAskedAfterB, "no further channel read took part");
        }
    }

    [Fact]
    public async Task Reconcile_ShouldLeaveTheCallWaiting_WhenTheOnlyReadWaitedOutItsBound()
    {
        await using var rig = await LossRig.StartAsync(attach: false);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        await rig.HoldAsync();
        await rig.ReconcileAsync();
        await rig.AdmitAsync();
        var call = rig.Manager.GetByLinkedId("L")!;
        var unverifiedAtAdmission = call.Unverified;
        var settled = 0;
        rig.Server.ChannelTableSettled += _ => Interlocked.Increment(ref settled);

        var waitedOut = await rig.RunAsync(() => rig.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            unverifiedAtAdmission.Should().BeTrue("premise: an earlier read and the admission's own read waited out their bound");
            waitedOut.Should().BeTrue("premise: the read's barrier ran out");
            rig.Server.ChannelTableCovered.Should().BeFalse("a read that waited out its bound leaves the table incomplete");
            settled.Should().Be(0);
            call.Unverified.Should().BeTrue("nothing covered the table: the call waits. " + rig.Describe(call));
            rig.EndingsOf(call).Should().Be(0);
        }

        rig.Release();
    }

    // A call with legs X and Y not observed in full; B held after its flag write; A run to its end; B released.
    private static async Task<Run> RunAsync(LossRig rig, bool deliveredLoss, bool legsGone)
    {
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();

        bool completeBeforeB;
        if (deliveredLoss)
        {
            await rig.HoldAndFillAsync();
            await rig.DropUnrelatedAsync();
            await rig.AdmitAsync();

            // Asterisk holds Z and the SDK was never told: only A's read, after the loss is delivered, lists it new.
            await rig.DropNewChannelAsync("Z", "M");
            await rig.DeliverAsync();
            completeBeforeB = rig.Server.ChannelTableComplete;
        }
        else
        {
            await rig.HoldAsync();
            await rig.ReconcileAsync();
            completeBeforeB = rig.Server.ChannelTableComplete;
            await rig.AdmitAsync();
        }

        var call = rig.Manager.GetByLinkedId("L")!;
        var unverifiedAtAdmission = call.Unverified;
        var pendingLoss = rig.Server.HasPendingLoss;
        var completions = 0;
        var settled = 0;
        rig.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completions);
        rig.Server.ChannelTableSettled += _ => Interlocked.Increment(ref settled);

        using var bWritten = new ManualResetEventSlim();
        using var bGo = new ManualResetEventSlim();
        using var aApplying = new ManualResetEventSlim();
        rig.Server.ChannelApplicationWritten = full =>
        {
            if (full || bWritten.IsSet) return;
            bWritten.Set();
            bGo.Wait(LossRig.Bound * 3);
        };

        Task a;
        Task b;
        if (deliveredLoss)
        {
            // A begins to apply first, so B is counted after it and A is not overlapped; A waits inside its application
            // until B has written its flag, so A's write comes last.
            rig.Server.Channels.ChannelAdded += channel =>
            {
                if (channel.UniqueId != "Z" || aApplying.IsSet) return;
                aApplying.Set();
                bWritten.Wait(LossRig.Bound * 3);
            };
            if (legsGone)
            {
                rig.ForgetChannel("X");
                rig.ForgetChannel("Y");
            }

            a = Task.Run(() => rig.Server.ReconcileChannelsAsync().AsTask());
            aApplying.Wait(LossRig.Bound).Should().BeTrue("premise: A began to apply");
            await rig.HoldAsync();
            b = await StartDeferredAsync(rig, bWritten);
            await a.WaitAsync(LossRig.Bound);
        }
        else
        {
            b = await StartDeferredAsync(rig, bWritten);
            if (legsGone)
            {
                rig.ForgetChannel("X");
                rig.ForgetChannel("Y");
            }

            rig.Release();
            a = rig.Server.ReconcileChannelsAsync().AsTask();
            await a.WaitAsync(LossRig.Bound);
        }

        var completeWhileBCounted = rig.Server.ChannelTableComplete;
        var completionsWhileBCounted = Volatile.Read(ref completions);
        bGo.Set();
        await b.WaitAsync(LossRig.Bound);
        var run = new Run(call, completeBeforeB, unverifiedAtAdmission, pendingLoss, completeWhileBCounted,
            completionsWhileBCounted, rig.Server.ChannelTableCovered, rig.StatusAsked);
        if (deliveredLoss) rig.Release();
        return run;
    }

    // Starts a channel read on the held dispatch, runs its barrier out on the rig's clock, and returns once the read has
    // written its flag and is held at the seam.
    private static async Task<Task> StartDeferredAsync(LossRig rig, ManualResetEventSlim written)
    {
        while (rig.LiveClock.TimersCreated.TryRead(out _))
        {
            // Only the barrier of this read is run out.
        }

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Server.FenceWaitObserved = (_, _) => waiting.TrySetResult();
        var b = Task.Run(() => rig.Server.ReconcileChannelsAsync().AsTask());
        await waiting.Task.WaitAsync(LossRig.Bound);
        rig.Server.FenceWaitObserved = null;
        await rig.LiveClock.TimersCreated.ReadAsync().AsTask().WaitAsync(LossRig.Bound);
        _ = Task.Run(() => rig.LiveClock.Advance(VerbaraServer.BarrierBound + TimeSpan.FromSeconds(1)));
        written.Wait(LossRig.Bound).Should().BeTrue("premise: the read whose barrier ran out wrote its flag");
        return b;
    }

    private sealed record Run(CallSession Call, bool CompleteBeforeB, bool UnverifiedAtAdmission, bool PendingLoss,
        bool CompleteWhileBCounted, int CompletionsWhileBCounted, bool CoveredAfterB, int StatusAskedAfterB)
    {
        public void AssertPremises(bool deliveredLoss)
        {
            UnverifiedAtAdmission.Should().BeTrue("premise: the call is not observed in full");
            if (deliveredLoss)
            {
                PendingLoss.Should().BeTrue("premise: a delivered loss no read has covered");
            }
            else
            {
                PendingLoss.Should().BeFalse("premise: no loss at any time");
                CompleteBeforeB.Should().BeFalse("premise: an earlier read waited out its bound");
            }

            CompleteWhileBCounted.Should().BeFalse("premise: B is still counted when A returns");
            CompletionsWhileBCounted.Should().Be(0, "premise: A left before B and did not announce the table");
            CoveredAfterB.Should().BeTrue("premise: the table reads covered once B has left");
        }
    }
}
