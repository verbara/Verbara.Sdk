using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A disposal of the sweep while one of its sweeps is reading Asterisk's <c>Status</c> answer tells that sweep to
/// stop, and a disposal that races the host's stop, or comes twice, throws nothing to either caller.
/// </summary>
/// <remarks>
/// The <c>Status</c> answer is held on a <see cref="TaskCompletionSource"/> until its token is cancelled or the test
/// releases it, so the sweep in flight is driven, never timed. Every wait is bounded, so a disposal that does not
/// cancel fails at the bound instead of hanging, and the hold is released on every way out.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepDisposalCancelsTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _held.TrySetResult();
        await _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);
    }

    [Fact]
    public async Task Dispose_ShouldCancelTheSweepInFlight_WhenASweepIsReadingItsStatusAnswer()
    {
        var sweep = await StartWithASweepInFlightAsync();

        sweep.Dispose();
        var cancelled = await Record.ExceptionAsync(() => _cancelled.Task.WaitAsync(SweepRig.Bound));

        cancelled.Should().BeNull(
            "a disposal cancels the token of the sweep in flight, so the Status read it is held on ends; a disposal "
            + $"that only releases the source leaves the sweep running. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Dispose_ShouldThrowNothingToEitherCaller_WhenItRacesTheStopAndComesTwice()
    {
        var sweep = await StartWithASweepInFlightAsync();
        using var go = new ManualResetEventSlim();

        var stop = Task.Run(async () =>
        {
            go.Wait();
            await sweep.StopAsync(CancellationToken.None);
        });
        var dispose = Task.Run(() =>
        {
            go.Wait();
            sweep.Dispose();
        });
        go.Set();
        var stopError = await Record.ExceptionAsync(() => stop.WaitAsync(SweepRig.Bound));
        var disposeError = await Record.ExceptionAsync(() => dispose.WaitAsync(SweepRig.Bound));
        var secondDisposeError = Record.Exception(sweep.Dispose);
        var cancelled = await Record.ExceptionAsync(() => _cancelled.Task.WaitAsync(SweepRig.Bound));

        using var scope = new AssertionScope();
        stopError.Should().BeNull("a stop that races a disposal either owns the source or finds it disposed");
        disposeError.Should().BeNull("a disposal that races a stop either owns the source or finds it taken");
        secondDisposeError.Should().BeNull("a second disposal does nothing");
        cancelled.Should().BeNull(
            "whichever of the two owned the source cancelled the sweep in flight. Measured: " + _rig.Describe());
    }

    /// <summary>
    /// Starts the sweep's loop over one call old enough to verify, and returns once a sweep sent its <c>Status</c> and
    /// is held reading the answer.
    /// </summary>
    private async Task<SessionReconciliationService> StartWithASweepInFlightAsync()
    {
        var old = _rig.DialingCall("old");
        _rig.Age(old);
        _rig.StatusReply = (_, token) => HeldUntilCancelled(token);
        var sweep = _rig.BuildSweep();
        await sweep.StartAsync(CancellationToken.None);
        (await _rig.WaitUntilAsync(() => _rig.StatusRequests >= 1)).Should().BeTrue(
            $"premise: a sweep sent its Status and is held reading it. Measured: {_rig.Describe()}");
        return sweep;
    }

    /// <summary>A <c>Status</c> answer that never comes: it records the cancellation of its token and ends with it.</summary>
    private async IAsyncEnumerable<ManagerEvent> HeldUntilCancelled(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await _held.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _cancelled.TrySetResult();
            throw;
        }

        yield break;
    }
}
