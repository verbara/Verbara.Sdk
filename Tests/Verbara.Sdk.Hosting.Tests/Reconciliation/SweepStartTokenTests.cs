using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The single-server sweep's loop runs on a source its service owns: the token handed to its start means only that
/// the start was aborted, so cancelling it once the start returned, or handing it already cancelled, does not stop
/// the sweep. The sweep's own stop still ends it (<see cref="SweepStopAfterDisposalTests"/> and every test that
/// stops a loop it started).
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepStartTokenTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public async Task Sweep_ShouldStillEndALostHangup_WhenItsStartTokenIsCancelledAfterTheStart()
    {
        // The loop ticks on a real 5 ms timer with no seam, so the call stays young — no candidate — until after the
        // cancel; it is aged only once the cancel returned. Two sweeps past the count noted at the cancel means at
        // least one whole sweep began after it, so a sweep already running at the cancel cannot stand in for one.
        var call = _rig.ConnectedCall("lost");
        _rig.AsteriskLists();
        using var start = new CancellationTokenSource();
        var loop = await _rig.StartLoopAsync(_rig.BuildSweep(), start.Token);

        await start.CancelAsync();
        var sweepsAtTheCancel = loop.Sweeps;
        _rig.Age(call);
        var swept = await _rig.WaitUntilAsync(
            () => loop.Sweeps >= sweepsAtTheCancel + 2 && call.State == CallSessionState.Completed);

        swept.Should().BeTrue(
            "the sweep did not run after its start token was cancelled: its loop runs on a source linked to that " +
            $"token. Sweeps at the cancel: {sweepsAtTheCancel}, now: {loop.Sweeps}. Measured: {_rig.Describe()}");
        new { call.State, Cause = call.Metadata.GetValueOrDefault("cause"), Endings = _rig.EndingsOf(call) }
            .Should().BeEquivalentTo(
                new { State = CallSessionState.Completed, Cause = "reload", Endings = 1 },
                $"the old held call whose channels Asterisk no longer lists ends as a reload ends it. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Sweep_ShouldStillEndALostHangup_WhenItsStartTokenIsAlreadyCancelled()
    {
        var call = _rig.ConnectedCall("lost");
        _rig.Age(call);
        _rig.AsteriskLists();

        var loop = await _rig.StartLoopAsync(_rig.BuildSweep(), new CancellationToken(canceled: true));
        var swept = await _rig.WaitUntilAsync(() => loop.Sweeps >= 1 && call.State == CallSessionState.Completed);

        swept.Should().BeTrue(
            "the sweep did not run after a start whose token was already cancelled: its loop runs on a source " +
            $"linked to that token, so it ended before its first tick. Sweeps: {loop.Sweeps}. Measured: {_rig.Describe()}");
        new { call.State, Cause = call.Metadata.GetValueOrDefault("cause"), Endings = _rig.EndingsOf(call) }
            .Should().BeEquivalentTo(
                new { State = CallSessionState.Completed, Cause = "reload", Endings = 1 },
                $"the old held call whose channels Asterisk no longer lists ends as a reload ends it. Measured: {_rig.Describe()}");
    }
}
