using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The sweep and a load of the same server that overlap: the sweep does not ask while a load is in flight and does
/// not wait for it, and two overlapping reconciliations never bring back a channel one of them removed.
/// </summary>
/// <remarks>
/// The load's <c>Status</c> answer is held on a <see cref="TaskCompletionSource"/> the test releases, so the
/// overlap is driven, never timed. Every wait is bounded, so a sweep that waits for the load fails at the bound
/// instead of hanging.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepWhileALoadIsInFlightTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Released on every way out, so no load the test began outlives it.
        _held.TrySetResult();
        await _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);
    }

    [Fact]
    public async Task Sweep_ShouldSendNoStatusNorWaitNorChangeASession_WhenALoadOfTheSameServerIsInFlight()
    {
        var old = _rig.DialingCall("old");
        _rig.Age(old);
        _rig.AsteriskLists("old");
        _rig.StatusReply = (number, token) => number == 1
            ? ListedOnceReleased(["old"], token)
            : _rig.ListedNow(["old"], token);
        var load = _rig.Server.RequestInitialStateAsync().AsTask();
        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 1)).Should().BeTrue(
            $"premise: the load sent its Status and is held reading it. Measured: {_rig.Describe()}");
        var before = SweepRig.Look(old);
        var sweep = _rig.BuildSweep();

        var sweepError = await Record.ExceptionAsync(() => SweepRig.SweepOnceAsync(sweep).WaitAsync(SweepRig.Bound));
        var measured = new
        {
            LoadStillInFlight = !load.IsCompleted,
            Status = _rig.StatusRequests,
            CallEndedEvents = _rig.Endings.Count,
            Call = SweepRig.Look(old),
        };
        var described = _rig.Describe();
        _held.TrySetResult();
        await load.WaitAsync(SweepRig.Bound);

        using var scope = new AssertionScope();
        sweepError.Should().BeNull("the sweep returns, without waiting for the load, inside the bound");
        measured.Should().BeEquivalentTo(
            new { LoadStillInFlight = true, Status = 1, CallEndedEvents = 0, Call = before },
            "while a load of the same server is in flight the sweep skips: it sends no Status of its own (the one "
            + "counted is the load's), returns without waiting for the load, and changes no session. "
            + $"Measured while the load was held: {described}");
    }

    [Fact]
    public async Task Reconciliations_ShouldNotAdmitAChannelAgain_WhenTheOtherRemovedItWhileTheOlderSnapshotWasRead()
    {
        var call = _rig.ConnectedCall("call");
        var linkedId = SweepRig.LinkedIdOf("call");

        // The first reconciliation reads a snapshot that still lists the call and is held there; the second reads one
        // that no longer does, and completes first.
        _rig.StatusReply = (number, token) => number == 1
            ? ListedOnceReleased(["call"], token)
            : _rig.ListedNow([], token);
        var older = ReconcileChannelsAsync();
        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 1)).Should().BeTrue(
            $"premise: the first reconciliation is reading its snapshot. Measured: {_rig.Describe()}");
        await ReconcileChannelsAsync().WaitAsync(SweepRig.Bound);
        var afterTheNewer = new
        {
            Held = _rig.UniqueIdsOf("call").Count(id => _rig.Server.Channels.GetByUniqueId(id) is not null),
            Ended = _rig.EndingsOf(call),
        };

        _held.TrySetResult();
        await older.WaitAsync(SweepRig.Bound);

        afterTheNewer.Should().BeEquivalentTo(
            new { Held = 0, Ended = 1 },
            $"premise: the newer snapshot removed both legs and ended the call once. Measured: {_rig.Describe()}");
        new
        {
            Held = _rig.UniqueIdsOf("call").Count(id => _rig.Server.Channels.GetByUniqueId(id) is not null),
            InProgress = _rig.Manager.ActiveSessions.Count(s => s.LinkedId == linkedId),
            Ended = _rig.EndingsOf(call),
            State = call.State,
        }.Should().BeEquivalentTo(
            new { Held = 0, InProgress = 0, Ended = 1, State = CallSessionState.Completed },
            "a channel one reconciliation removed is not admitted again by the other's older snapshot: no leg is held "
            + $"again, no call is opened for it, and the ending is not repeated. Measured: {_rig.Describe()}");
    }

    /// <summary>
    /// A reconciliation of the server's channels against Asterisk's <c>Status</c>: the full load, which reconciles
    /// the channel table on its way to the queues and the agents.
    /// </summary>
    private Task ReconcileChannelsAsync() => _rig.Server.RequestInitialStateAsync().AsTask();

    /// <summary>A <c>Status</c> answer listing <paramref name="calls"/>, held until the test releases it.</summary>
    private async IAsyncEnumerable<ManagerEvent> ListedOnceReleased(
        IReadOnlyList<string> calls, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _held.Task.WaitAsync(cancellationToken);
        await foreach (var status in _rig.ListedNow(calls, cancellationToken))
            yield return status;
    }
}
