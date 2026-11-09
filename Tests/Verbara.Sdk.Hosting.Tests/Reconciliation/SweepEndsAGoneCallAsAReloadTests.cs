using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A call the sweep's verification proves gone — Asterisk answers <c>Status</c> in full and lists none of its
/// channels — ends as a reload ends it: <c>Completed</c> if it was up, <c>Failed</c> otherwise, no hangup
/// cause, <c>cause=reload</c>, and its ending delivered once (event, release entry, save, one count).
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepEndsAGoneCallAsAReloadTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    /// <summary>What a test reads of a call after the sweep.</summary>
    private sealed record Ending(
        CallSessionState State,
        HangupCause? HangupCause,
        string? Cause,
        bool HasTalkTime,
        int CallEndedEvents,
        int ReleaseEntries,
        bool SavedEnded);

    private Ending EndingOf(CallSession call) => new(
        call.State,
        call.HangupCause,
        call.Metadata.GetValueOrDefault("cause"),
        call.TalkTime.HasValue,
        _rig.EndingsOf(call),
        _rig.Manager.ReleaseQueueEntriesFor(call.SessionId),
        _rig.RecordingStore!.SavesOf(call.SessionId).Contains(call.State)
            && call.State is CallSessionState.Completed or CallSessionState.Failed);

    [Fact]
    public async Task Sweep_ShouldEndAnUnansweredCallFailedAsAReload_WhenAsteriskNoLongerListsItsChannels()
    {
        var call = _rig.DialingCall("gone");
        _rig.Age(call);
        _rig.AsteriskLists();
        var sweep = _rig.BuildSweep();
        using var counters = new SessionCounters();

        await SweepRig.SweepOnceAsync(sweep);

        new { Ending = EndingOf(call), Counters = counters.Deltas }.Should().BeEquivalentTo(
            new
            {
                Ending = new Ending(CallSessionState.Failed, null, "reload", false, 1, 1, true),
                Counters = CounterDeltas.None with { Failed = 1 },
            },
            "a completed Status that lists none of the call's channels proves it gone, and the reload's ending "
            + $"delivers it once: event, release entry, save, one count. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Sweep_ShouldEndAConnectedCallCompletedAsAReload_WhenItsHangupWasLost()
    {
        var call = _rig.ConnectedCall("talked");
        _rig.Age(call);
        _rig.AsteriskLists();
        var sweep = _rig.BuildSweep();
        using var counters = new SessionCounters();

        await SweepRig.SweepOnceAsync(sweep);

        new { Ending = EndingOf(call), Counters = counters.Deltas }.Should().BeEquivalentTo(
            new
            {
                Ending = new Ending(CallSessionState.Completed, null, "reload", true, 1, 1, true),
                Counters = CounterDeltas.None with { Completed = 1 },
            },
            "a call that was up and that Asterisk no longer lists took place and is over: it ends completed, "
            + $"with its talk time, through the reload's ending. Measured: {_rig.Describe()}");
    }

    public enum Shape
    {
        Dialing,
        Connected,
    }

    [Theory]
    [InlineData(Shape.Dialing)]
    [InlineData(Shape.Connected)]
    public async Task Sweep_ShouldDeliverTheEndingOnce_WhenTheLostHangupsArriveAfterTheVerification(Shape shape)
    {
        var call = shape == Shape.Dialing ? _rig.DialingCall("late") : _rig.ConnectedCall("late");
        _rig.Age(call);
        _rig.AsteriskLists();
        var sweep = _rig.BuildSweep();
        using var counters = new SessionCounters();
        await SweepRig.SweepOnceAsync(sweep);
        var atTheSweep = counters.Deltas;

        _rig.HangUp("late", HangupCause.NormalClearing);

        new
        {
            CallEndedEvents = _rig.EndingsOf(call),
            ReleaseEntries = _rig.Manager.ReleaseQueueEntriesFor(call.SessionId),
            CountedByTheHangups = counters.Deltas.Since(atTheSweep),
        }.Should().BeEquivalentTo(
            new { CallEndedEvents = 1, ReleaseEntries = 1, CountedByTheHangups = CounterDeltas.None },
            $"the {shape} call's ending was delivered by the verification; Hangups that arrive afterwards change "
            + $"no count and deliver nothing again (at the sweep: {atTheSweep}). Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Sweep_ShouldLetTheManagerAndTheDefaultStoreReleaseTheCall_WhenItsRetentionHasPassed()
    {
        await using var rig = new SweepRig(defaultStore: true);
        var call = rig.DialingCall("kept");
        rig.Age(call);
        rig.AsteriskLists();
        var sweep = rig.BuildSweep();
        await SweepRig.SweepOnceAsync(sweep);

        rig.Clock.Advance(rig.Options.CompletedRetention + TimeSpan.FromHours(1));
        rig.CreatedCall("next");

        new
        {
            HeldByTheManager = rig.Manager.GetById(call.SessionId) is not null,
            HeldByTheDefaultStore = await rig.DefaultStore!.GetAsync(call.SessionId, CancellationToken.None) is not null,
        }.Should().BeEquivalentTo(
            new { HeldByTheManager = false, HeldByTheDefaultStore = false },
            $"the verification's ending queued the call for release, so past retention both let it go "
            + $"(state {call.State}). Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Sweep_ShouldEndAQueuedCallOnceAndCountItAbandonedOnce_WhenItsHangupWasLost()
    {
        using var tracker = new QueueSessionTracker(_rig.Manager, Options.Create(_rig.Options));
        var call = _rig.QueuedCall("waiting", "support");
        _rig.Age(call);
        _rig.AsteriskLists();
        var sweep = _rig.BuildSweep();

        await SweepRig.SweepOnceAsync(sweep);
        await SweepRig.SweepOnceAsync(sweep);

        // Recorded, not asserted: a channels-only verification sends no QueueStatus, so Live's queue table may
        // keep its waiting entry until the next full load.
        output.WriteLine(
            $"Live QueueManager waiting entries for 'support' after the sweeps: {_rig.Server.Queues.GetByName("support")?.EntryCount}");

        new
        {
            State = call.State,
            Cause = call.Metadata.GetValueOrDefault("cause"),
            CallEndedEvents = _rig.EndingsOf(call),
            Abandoned = tracker.GetByQueueName("support")?.CallsAbandoned,
        }.Should().BeEquivalentTo(
            new { State = CallSessionState.Failed, Cause = "reload", CallEndedEvents = 1, Abandoned = 1 },
            "a queued call whose hangup was lost ends once through the verification, and the queue counts it "
            + $"abandoned once. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Sweep_ShouldSendNothingAndKeepCountingTheObservedCall_WhenItsRemovalHasNotReachedItsLock()
    {
        var call = _rig.CreatedCall("observed");
        _rig.Age(call);
        call.Unverified.Should().BeFalse();
        _rig.Manager.IsPendingDecision(call).Should().BeFalse();
        var before = SweepRig.Look(call);
        var sweep = _rig.BuildSweep();
        using var parked = StepParker.Install(_rig.Manager, HandlerStep.RemovalFound, SweepRig.ServerId, call.Participants.Single().UniqueId);
        var removal = Task.Run(() => _rig.HangUp("observed", HangupCause.NormalClearing));
        try
        {
            parked.WaitUntilParked();
            _rig.Server.Channels.GetByUniqueId(call.Participants.Single().UniqueId).Should().BeNull();
            var tags = await SweepSwitchesAndReportsTests.SweepTagsAsync(() => SweepRig.SweepOnceAsync(sweep));
            _rig.Sent<StatusAction>().Should().Be(0);
            tags["sessions.unverifiable"].Should().Be(1);
            SweepRig.Look(call).Should().BeEquivalentTo(before);
            _rig.EndingsOf(call).Should().Be(0);
        }
        finally { parked.LetGo(); await removal.WaitAsync(SweepRig.Bound); }
        _rig.EndingsOf(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
        var after = await SweepSwitchesAndReportsTests.SweepTagsAsync(() => SweepRig.SweepOnceAsync(sweep));
        after["sessions.unverifiable"].Should().Be(0);
        _rig.EndingsOf(call).Should().Be(1);
        SessionIndexInvariant.Assert(_rig.Manager);
    }

    [Fact]
    public async Task Sweep_ShouldAskOnceAndLeaveTheObservedChannelLessCall_WhenAnOrdinaryCandidateIsHeldBesideIt()
    {
        var call = _rig.CreatedCall("observed");
        _rig.Age(call);
        call.Unverified.Should().BeFalse();
        _rig.Manager.IsPendingDecision(call).Should().BeFalse();
        var ordinary = _rig.DialingCall("ordinary");
        _rig.Age(ordinary);
        _rig.AsteriskLists("ordinary");
        var before = SweepRig.Look(call);
        var sweep = _rig.BuildSweep();
        using var parked = StepParker.Install(_rig.Manager, HandlerStep.RemovalFound, SweepRig.ServerId, call.Participants.Single().UniqueId);
        var removal = Task.Run(() => _rig.HangUp("observed", HangupCause.NormalClearing));
        try
        {
            parked.WaitUntilParked();
            _rig.Server.Channels.GetByUniqueId(call.Participants.Single().UniqueId).Should().BeNull();
            var tags = await SweepSwitchesAndReportsTests.SweepTagsAsync(() => SweepRig.SweepOnceAsync(sweep));
            _rig.Sent<StatusAction>().Should().Be(1);
            tags["sessions.unverifiable"].Should().Be(1);
            SweepRig.Look(call).Should().BeEquivalentTo(before);
            _rig.EndingsOf(call).Should().Be(0);
        }
        finally { parked.LetGo(); await removal.WaitAsync(SweepRig.Bound); }
        _rig.EndingsOf(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
        var after = await SweepSwitchesAndReportsTests.SweepTagsAsync(() => SweepRig.SweepOnceAsync(sweep));
        after["sessions.unverifiable"].Should().Be(0);
        _rig.EndingsOf(call).Should().Be(1);
        SessionIndexInvariant.Assert(_rig.Manager);
    }

}
