using System.Diagnostics.CodeAnalysis;
using FluentAssertions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A stop of the sweep after it was disposed does nothing and throws nothing, as the host may stop a service it has
/// already disposed.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepStopAfterDisposalTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public async Task StopAsync_ShouldNotThrow_WhenTheSweepWasDisposedAfterItsStart()
    {
        // Started, then disposed: the using block ends right after the start.
        SweepLoop loop;
        using (var sweep = _rig.BuildSweep())
        {
            loop = await _rig.StartLoopAsync(sweep);
        }

        var error = await Record.ExceptionAsync(loop.StopAsync);

        error.Should().BeNull(
            "a stop after disposal is a no-op: it must not cancel the source the disposal released");
    }
}
