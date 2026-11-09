using System.Reactive.Linq;
using System.Reflection;
using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class ScopedSweepIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldCountTheAdmissionCallUntilCompletion_WhenItsActualStatusIsRefused(bool legless)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.ConnectAsync(peer);
        await using var rig = new PoolSweepRig();
        if (!legless) run.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        rig.Manager.AttachToServer(run.Server, "a");
        if (legless)
            rig.Manager.RegisterReconstructedSession(new CallSession("admission-legless", "L", "a", CallDirection.Inbound)).Should().BeTrue();
        await rig.Manager.AdmitExistingCalls("a");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        call.Should().NotBeNull();
        run.Server.LastStatusRefusal.Should().NotBeNull();
        if (legless) call.Participants.Should().BeEmpty();
        else
        {
            call.Participants.Should().ContainSingle().Which.UniqueId.Should().Be("X");
            run.Server.Channels.OnHangup("X", HangupCause.UserBusy);
            call.Participants.Single().LeftAt.Should().NotBeNull();
        }
        rig.Manager.IsPendingDecision(call).Should().BeTrue();
        rig.EndingsOf(call).Should().Be(0, "an unverified admission never ends on an incomplete table");
        for (var i = 0; i < 2; i++)
        {
            await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
            ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(1);
            rig.EndingsOf(call).Should().Be(0);
        }
        peer.StatusRefusedFromAsk = 0;
        await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        rig.EndingsOf(call).Should().Be(1);
        call.HangupCause.Should().Be(legless ? null : HangupCause.UserBusy);
        if (legless) call.Metadata["cause"].Should().Be("reload");
        else call.Metadata.Should().NotContainKey("cause");
        await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
        rig.EndingsOf(call).Should().Be(1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
        peer.Fault.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldCountZeroDuringTheUnfinishedAndDeferredLoad_WhenItsSuccessfulReadIsStillWaiting(bool priorRefusal)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = priorRefusal ? 1 : 0 };
        var barrierClock = new FakeTimeProvider();
        await using var run = await Run.ConnectAsync(peer, barrierClock: barrierClock);
        await using var rig = new PoolSweepRig();
        rig.Manager.AttachToServer(run.Server, "a");
        var call = new CallSession("waiting", "L", "a", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        if (priorRefusal)
        {
            await rig.Manager.AdmitExistingCalls("a");
            run.Server.LastStatusRefusal.Should().NotBeNull();
        }
        else run.Server.LastStatusRefusal.Should().BeNull();
        peer.StatusRefusedFromAsk = 0;
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(VerbaraServer).GetProperty("FenceWaitObserved", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(run.Server, (Action<string, long>)((_, _) => waiting.TrySetResult()));
        var load = run.StartServerAsync();
        Task admission = Task.CompletedTask;
        try
        {
            await waiting.Task.WaitAsync(Run.Bound);
            run.Server.LastStatusRefusal.Should().BeNull("the actual Status read completed before the held dispatch returned");
            ScopedSweepDecisionTests.Complete(run.Server).Should().BeFalse();
            var asked = peer.Asked("Status");
            admission = rig.Manager.AdmitExistingCalls("a").AsTask();
            await peer.AskedAtLeast("Status", asked + 1).WaitAsync(Run.Bound);
            admission.IsCompleted.Should().BeFalse("the real admission's successful snapshot still waits behind the held dispatch");
            await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
            ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
            ScopedSweepDecisionTests.Count(rig, "Candidates").Should().Be(1);
            load.IsCompleted.Should().BeFalse();
            await FinishDeferred(Task.WhenAll(load, admission), barrierClock);
            ScopedSweepDecisionTests.Complete(run.Server).Should().BeFalse();
            rig.EndingsOf(call).Should().Be(0);
            var tick = ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
            await FinishDeferred(tick, barrierClock);
            ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
            rig.EndingsOf(call).Should().Be(0);
        }
        finally { gate.Release(); await Task.WhenAll(load, admission).WaitAsync(Run.Bound); }
        await run.Server.ScheduledReload.WaitAsync(Run.Bound);
        await run.Server.ReconcileChannelsAsync();
        rig.EndingsOf(call).Should().Be(1);
        call.Metadata["cause"].Should().Be("reload");
        await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        peer.Fault.Should().BeNull();
    }

    private static async Task FinishDeferred(Task operation, FakeTimeProvider clock)
    {
        while (!operation.IsCompleted)
        {
            using var stop = new CancellationTokenSource();
            var timer = clock.TimersCreated.ReadAsync(stop.Token).AsTask();
            var first = await Task.WhenAny(operation, timer).WaitAsync(Run.Bound);
            await stop.CancelAsync();
            if (first == timer && timer.IsCompletedSuccessfully) clock.Advance((await timer).DueTime);
        }
        await operation.WaitAsync(Run.Bound);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sweep_ShouldNeverCountTheRelabelledCall_WhenAdmissionKeepsItsChannelOwner(bool admit, bool otherCall)
    {
        await using var first = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true });
        await using var rig = new PoolSweepRig();
        var published = new List<CallStartedEvent>();
        using var starts = rig.Manager.Events.OfType<CallStartedEvent>().Subscribe(published.Add);
        rig.Manager.AttachToServer(first.Server, "a");
        first.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        first.Server.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        rig.Manager.DetachFromServer("a");
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [
            new StatusChannel("X", "PJSIP/X", "L-prime"), new StatusChannel("Y", "PJSIP/Y", "L-prime")] };
        await using var replacement = await Run.StartAsync(peer);
        var startsBeforeAttach = published.Count;
        rig.Manager.AttachToServer(replacement.Server, "a");
        published.Should().HaveCount(startsBeforeAttach);
        CallSession? other = null;
        if (otherCall)
        {
            replacement.Server.Channels.OnNewChannel("Z", "PJSIP/Z", ChannelState.Up, linkedId: "L-prime");
            other = rig.Manager.GetByLinkedId("a", "L-prime");
            peer.StatusChannels = [.. peer.StatusChannels, new StatusChannel("Z", "PJSIP/Z", "L-prime")];
        }
        var startsBeforeAdmission = published.Count;
        var sessionsBeforeAdmission = rig.Manager.InspectIndexes().Sessions.Count;
        var beforeOther = other is null ? null : SweepRig.Look(other);
        if (admit) await rig.Manager.AdmitExistingCalls("a");
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        await ScopedSweepDecisionTests.Pass(rig, replacement.Server, "a");
        ScopedSweepDecisionTests.Count(rig, "Candidates").Should().Be(1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        rig.Manager.GetByChannelId("a", "X").Should().BeSameAs(call);
        rig.Manager.GetByChannelId("a", "Y").Should().BeSameAs(call);
        rig.Manager.InspectIndexes().Sessions.Should().HaveCount(sessionsBeforeAdmission);
        published.Should().HaveCount(startsBeforeAdmission);
        if (other is not null)
        {
            other.Participants.Select(p => p.UniqueId).Should().Equal("Z");
            rig.Manager.GetByChannelId("a", "Z").Should().BeSameAs(other);
            SweepRig.Look(other).Should().BeEquivalentTo(beforeOther);
        }
        replacement.Server.Channels.OnHangup("X", HangupCause.NormalClearing);
        replacement.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
        rig.EndingsOf(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.UserBusy);
        if (other is not null)
        {
            other.Participants.Should().ContainSingle().Which.UniqueId.Should().Be("Z");
            rig.Manager.GetByChannelId("a", "Z").Should().BeSameAs(other);
            var afterOther = SweepRig.Look(other);
            afterOther.State.Should().Be(beforeOther!.State);
            afterOther.Cause.Should().Be(beforeOther.Cause);
            afterOther.HangupCause.Should().Be(beforeOther.HangupCause);
            var expectedDepartures = new (CallSessionEventType Type, string? SourceChannel, string? TargetChannel, string? Detail)[]
            {
                (CallSessionEventType.ParticipantLeft, "PJSIP/X", null, HangupCause.NormalClearing.ToString()),
                (CallSessionEventType.ParticipantLeft, "PJSIP/Y", null, HangupCause.UserBusy.ToString()),
            };
            other.Events.Skip(beforeOther.AuditEntries).Select(e => (e.Type, e.SourceChannel, e.TargetChannel, e.Detail))
                .Should().Equal(expectedDepartures);
            rig.EndingsOf(other).Should().Be(0);
            published.Should().HaveCount(startsBeforeAdmission);
            peer.StatusChannels = [new StatusChannel("Z", "PJSIP/Z", "L-prime")];
        }
        else peer.StatusChannels = [];
        await ScopedSweepDecisionTests.Pass(rig, replacement.Server, "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldKeepTheDetachedLeglessCallAndItsFinalCause_WhenTheReturningTableCarriesItsLinkedId(bool admit)
    {
        await using var rig = new PoolSweepRig();
        var published = new List<CallStartedEvent>();
        using var starts = rig.Manager.Events.OfType<CallStartedEvent>().Subscribe(published.Add);
        var call = new CallSession("legless-return", "L", "a", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [
            new StatusChannel("X", "PJSIP/X", "L"), new StatusChannel("Y", "PJSIP/Y", "L")] };
        await using var run = await Run.StartAsync(peer);
        var startsBeforeAttach = published.Count;
        rig.Manager.AttachToServer(run.Server, "a");
        rig.EndingsOf(call).Should().Be(0);
        call.Participants.Should().BeEmpty("the attach decision does not perform admission");
        if (admit) await rig.Manager.AdmitExistingCalls("a");
        call.Participants.Select(p => p.UniqueId).Should().BeEquivalentTo(admit ? ["X", "Y"] : Array.Empty<string>());
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
        ScopedSweepDecisionTests.Count(rig, "Candidates").Should().Be(1);
        published.Should().HaveCount(startsBeforeAttach);
        rig.Manager.InspectIndexes().Sessions.Should().ContainSingle().Which.Value.Should().BeSameAs(call);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        rig.EndingsOf(call).Should().Be(0);
        run.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        rig.EndingsOf(call).Should().Be(0);
        run.Server.Channels.OnHangup("Y", HangupCause.NormalClearing);
        rig.EndingsOf(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
        peer.StatusChannels = [];
        await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        rig.Manager.ActiveSessions.Should().NotContain(c => c.ServerId == "a");
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task Sweep_ShouldVerifyByLinkedId_WhenARegisteredDetachedCallHasNoJoinedLeg()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [new StatusChannel("X", "PJSIP/X", "L")] };
        await using var run = await Run.StartAsync(peer);
        await using var rig = new PoolSweepRig();
        var call = new CallSession("legless", "L", "a", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Manager.AttachToServer(run.Server, "a");
        call.Participants.Should().BeEmpty();
        rig.EndingsOf(call).Should().Be(0);
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        peer.StatusChannels = [];
        var before = peer.Asked("Status");
        await ScopedSweepDecisionTests.Pass(rig, run.Server, "a");
        peer.Asked("Status").Should().Be(before + 1);
        rig.EndingsOf(call).Should().Be(1);
        call.Metadata["cause"].Should().Be("reload");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }

}
