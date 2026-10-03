using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A verification the sweep cannot trust ends nothing, and the sweep's loop survives it: a connection that is
/// not established, a snapshot cancelled mid-read by the host's stop, a snapshot that fails with a cancellation
/// that is not the loop's own, and a completed snapshot whose endings reach a subscriber that throws.
/// </summary>
/// <remarks>
/// These run the sweep's own loop, as the host starts it, and count what it does: the <c>Status</c> requests it
/// sends and the sweeps it completes. Every wait ends on one of those signals; its bound only turns a signal that
/// never comes into a failure instead of a hang. A wait for the loop to have verified ends early once the loop
/// has completed as many sweeps, so a sweep that ended the calls without asking fails on the calls, not on the
/// bound.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepUntrustedVerificationTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    /// <summary>Two old candidates whose channels Asterisk still lists: a dialing call and an unanswered one.</summary>
    private (CallSession Dialing, CallSession Created) TwoOldCandidates()
    {
        var dialing = _rig.DialingCall("dialing");
        _rig.Age(dialing);
        var created = _rig.CreatedCall("created");
        _rig.Age(created);
        _rig.AsteriskLists("dialing", "created");
        return (dialing, created);
    }

    [Fact]
    public async Task Sweep_ShouldEndNothingAndSendNoStatus_WhenTheConnectionIsNotEstablished()
    {
        var (dialing, created) = TwoOldCandidates();
        var before = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) };
        _rig.ConnectionState = AmiConnectionState.Reconnecting;
        var loop = await _rig.StartLoopAsync(_rig.BuildSweep());

        (await _rig.WaitUntilAsync(() => loop.Sweeps >= 2)).Should().BeTrue(
            $"premise: the loop completes its sweeps. Measured: {_rig.Describe()}");
        new
        {
            Status = _rig.StatusRequests,
            CallEndedEvents = _rig.Endings.Count,
            Calls = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) },
        }.Should().BeEquivalentTo(
            new { Status = 0, CallEndedEvents = 0, Calls = before },
            "a connection that is not established cannot verify anything, so the sweep skips: it asks nothing "
            + $"and ends nothing. Measured: {_rig.Describe()}");

        _rig.ConnectionState = AmiConnectionState.Connected;

        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 1)).Should().BeTrue(
            $"the loop survives the skipped sweeps: once connected, a next tick verifies. Measured: {_rig.Describe()}");
        await loop.StopAsync();
    }

    [Fact]
    public async Task Sweep_ShouldEndNothing_WhenTheHostsStopCancelsTheSnapshotMidRead()
    {
        var (dialing, created) = TwoOldCandidates();
        var before = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) };
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.StatusReply = (number, token) => number == 1
            ? HeldUntil(held.Task, token)
            : _rig.ListedNow(["dialing", "created"], token);
        var loop = await _rig.StartLoopAsync(_rig.BuildSweep());

        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 1 || loop.Sweeps >= 1)).Should().BeTrue(
            $"premise: the loop's first sweep runs. Measured: {_rig.Describe()}");
        await loop.StopAsync();
        held.TrySetResult();

        new
        {
            CallEndedEvents = _rig.Endings.Count,
            Calls = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) },
        }.Should().BeEquivalentTo(
            new { CallEndedEvents = 0, Calls = before },
            "the host's stop cancelled the snapshot before it completed, and an unfinished snapshot is no "
            + $"evidence that any channel is gone. Measured: {_rig.Describe()}");

        await loop.StartAsync();
        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 2)).Should().BeTrue(
            $"started again, the sweep verifies at its next tick. Measured: {_rig.Describe()}");
        await loop.StopAsync();
    }

    [Fact]
    public async Task Sweep_ShouldEndNothingAndKeepVerifying_WhenTheSnapshotFailsWithACancellationThatIsNotTheLoops()
    {
        var (dialing, created) = TwoOldCandidates();
        var before = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) };
        _rig.StatusReply = (number, token) => number == 1
            ? EventTimeoutElapses()
            : _rig.ListedNow(["dialing", "created"], token);
        var loop = await _rig.StartLoopAsync(_rig.BuildSweep());

        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 2 || loop.Sweeps >= 2)).Should().BeTrue(
            $"premise: the loop completes its sweeps. Measured: {_rig.Describe()}");
        new
        {
            CallEndedEvents = _rig.Endings.Count,
            Calls = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) },
        }.Should().BeEquivalentTo(
            new { CallEndedEvents = 0, Calls = before },
            "a snapshot that failed is no evidence that any channel is gone, and a later one lists them all. "
            + $"Measured: {_rig.Describe()}");
        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 2)).Should().BeTrue(
            "a cancellation that is not the loop's own (the connection's event timeout) does not stop the loop: "
            + $"the next tick verifies again. Measured: {_rig.Describe()}");
        await loop.StopAsync();
    }

    [Fact]
    public async Task Sweep_ShouldEndTheDroppedCallsAndKeepVerifying_WhenACallEndedSubscriberThrows()
    {
        var droppedDialing = _rig.DialingCall("dropped-dialing");
        _rig.Age(droppedDialing);
        var droppedConnected = _rig.ConnectedCall("dropped-connected");
        _rig.Age(droppedConnected);
        var kept = _rig.DialingCall("kept");
        _rig.Age(kept);
        var keptBefore = SweepRig.Look(kept);
        _rig.AsteriskLists("kept");
        using var throwing = _rig.Manager.Events.Subscribe(new ThrowsOnCallEnded());
        var loop = await _rig.StartLoopAsync(_rig.BuildSweep());

        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 3 || loop.Sweeps >= 3)).Should().BeTrue(
            $"premise: the loop completes its sweeps. Measured: {_rig.Describe()}");
        new
        {
            DroppedDialing = new { droppedDialing.State, Cause = droppedDialing.Metadata.GetValueOrDefault("cause"), Endings = _rig.EndingsOf(droppedDialing) },
            DroppedConnected = new { droppedConnected.State, Cause = droppedConnected.Metadata.GetValueOrDefault("cause"), Endings = _rig.EndingsOf(droppedConnected) },
            Kept = SweepRig.Look(kept),
        }.Should().BeEquivalentTo(
            new
            {
                DroppedDialing = new { State = CallSessionState.Failed, Cause = "reload", Endings = 1 },
                DroppedConnected = new { State = CallSessionState.Completed, Cause = "reload", Endings = 1 },
                Kept = keptBefore,
            },
            "a subscriber that throws on an ending interrupts that verification, not the loop: the next one ends "
            + $"what the first left, and the call Asterisk still lists is left alone. Measured: {_rig.Describe()}");
        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 3)).Should().BeTrue(
            $"the loop keeps verifying the call Asterisk still lists. Measured: {_rig.Describe()}");
        await loop.StopAsync();
    }

    /// <summary>A <c>Status</c> answer that lists nothing until <paramref name="release"/>, honouring its token.</summary>
    private static async IAsyncEnumerable<ManagerEvent> HeldUntil(
        Task release, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await release.WaitAsync(cancellationToken);
        foreach (var none in Array.Empty<ManagerEvent>())
            yield return none;
    }

    /// <summary>
    /// A <c>Status</c> answer that fails as the connection's own event timeout fails it: an
    /// <see cref="OperationCanceledException"/> carrying a token that is not the sweep loop's.
    /// </summary>
    private static async IAsyncEnumerable<ManagerEvent> EventTimeoutElapses()
    {
        await Task.Yield();
        foreach (var none in Array.Empty<ManagerEvent>())
            yield return none;

        throw new OperationCanceledException(
            "The connection's event timeout elapsed before StatusComplete.", new CancellationToken(canceled: true));
    }

    private sealed class ThrowsOnCallEnded : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallEndedEvent)
                throw new InvalidOperationException("A CallEndedEvent subscriber failed.");
        }

        public void OnError(Exception error)
        {
            // The manager's subject never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to do.
        }
    }
}
