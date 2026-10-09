using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The pool sweep's loop runs on a source its service owns: a start token cancelled after the start does not stop it,
/// and a stop after disposal, or a second disposal, does nothing.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class PoolSweepLifetimeTests : IAsyncLifetime
{
    private readonly PoolSweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public async Task PoolSweep_ShouldStillEndALostHangup_WhenItsStartTokenIsCancelledAfterTheStart()
    {
        var a = _rig.AddServer("a");
        var call = a.LostCall(_rig.Manager, "lost");
        a.AsteriskLists();
        using var start = new CancellationTokenSource();
        await _rig.StartAsync(start.Token);
        await start.CancelAsync();

        await _rig.TickAsync();
        await _rig.TickAsync();

        new { call.State, Cause = call.Metadata.GetValueOrDefault("cause"), Endings = _rig.EndingsOf(call) }
            .Should().BeEquivalentTo(
                new { State = CallSessionState.Completed, Cause = "reload", Endings = 1 },
                "the start token means only that the start was aborted; the loop runs on the service's own source. "
                + $"Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldStopAndDisposeAgainWithoutAnException_WhenItWasDisposedAfterItsStart()
    {
        var sweep = _rig.PoolSweep;
        sweep.Should().NotBeNull("the multi-server registration registers the pool sweep");
        _rig.AddServer("a");
        await sweep!.StartAsync(CancellationToken.None);
        ((IDisposable)sweep).Dispose();

        var stop = await Record.ExceptionAsync(() => sweep.StopAsync(CancellationToken.None).WaitAsync(SweepRig.Bound));
        var secondDisposal = Record.Exception(((IDisposable)sweep).Dispose);

        new { stop, secondDisposal }.Should().BeEquivalentTo(
            new { stop = default(Exception), secondDisposal = default(Exception) },
            "a stop after the disposal cancels and awaits nothing it released, and a second disposal is ignored");
    }
}
