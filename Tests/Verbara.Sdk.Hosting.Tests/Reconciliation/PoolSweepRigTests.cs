using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The pool rig measures what the pool sweep's tests read: a server's <c>Status</c> count and what its reconciliation
/// ends, the clock's timers, and nothing sent when nothing asks.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class PoolSweepRigTests : IAsyncLifetime
{
    private readonly PoolSweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public async Task Rig_ShouldCountOneStatusAndEndTheLostCall_WhenAServerReconcilesItsChannelsDirectly()
    {
        var a = _rig.AddServer("a");
        _rig.AddServer("b");
        var call = a.LostCall(_rig.Manager, "lost");
        a.AsteriskLists();

        await a.Server.ReconcileChannelsAsync().AsTask().WaitAsync(SweepRig.Bound);

        new { a.StatusRequests, B = _rig["b"].StatusRequests, call.State, Cause = call.Metadata.GetValueOrDefault("cause"), Endings = _rig.EndingsOf(call) }
            .Should().BeEquivalentTo(
                new { StatusRequests = 1, B = 0, State = CallSessionState.Completed, Cause = (string?)"reload", Endings = 1 },
                "the rig counts each server's Status on its own and its servers end a gone call through the reload ending");
    }

    [Fact]
    public async Task Rig_ShouldSendNoStatus_WhenTwoServersHoldNoCall()
    {
        var a = _rig.AddServer("a");
        var b = _rig.AddServer("b");
        await _rig.StartAsync();

        await _rig.TickAsync();

        new { A = a.StatusRequests, B = b.StatusRequests }.Should().BeEquivalentTo(
            new { A = 0, B = 0 }, $"no call qualifies on either server. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Clock_ShouldFireAPeriodicTimerOncePerAdvance_WhenAPeriodicTimerRunsOnIt()
    {
        using var timer = new PeriodicTimer(PoolSweepRig.Interval, _rig.Clock);
        var first = timer.WaitForNextTickAsync().AsTask();
        var beforeAdvance = first.IsCompleted;

        _rig.Clock.Advance(PoolSweepRig.Interval);
        await first.WaitAsync(SweepRig.Bound);
        var second = timer.WaitForNextTickAsync().AsTask();
        var secondBeforeAdvance = second.IsCompleted;
        _rig.Clock.Advance(PoolSweepRig.Interval);
        await second.WaitAsync(SweepRig.Bound);

        new { beforeAdvance, secondBeforeAdvance, _rig.Clock.TimersCreated }.Should().BeEquivalentTo(
            new { beforeAdvance = false, secondBeforeAdvance = false, TimersCreated = 1 },
            "a tick comes only when the test moves the clock, once per interval");
    }
}
