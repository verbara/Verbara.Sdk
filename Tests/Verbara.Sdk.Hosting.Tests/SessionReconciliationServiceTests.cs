using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Hosting.Tests.Reconciliation;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// The sweep leaves a call younger than the dialing timeout alone and asks nothing for it, and its loop stops when
/// the host stops it. Built and run through the reconciliation rig's one sweep helper.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SessionReconciliationServiceTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public async Task Sweep_ShouldNotTouch_WhenSessionConnected()
    {
        var session = _rig.ConnectedCall("connected");
        var before = SweepRig.Look(session);
        var sweep = _rig.BuildSweep();

        await SweepRig.SweepOnceAsync(sweep);

        new { Session = SweepRig.Look(session), Actions = _rig.ActionsSent().Count }.Should().BeEquivalentTo(
            new { Session = before, Actions = 0 },
            $"a connected call younger than the dialing timeout is not a candidate. Measured: {_rig.Describe()}");
        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public async Task Sweep_ShouldNotTouch_WhenDialingWithinTimeout()
    {
        var session = _rig.DialingCall("dialing");
        var before = SweepRig.Look(session);
        var sweep = _rig.BuildSweep();

        await SweepRig.SweepOnceAsync(sweep);

        new { Session = SweepRig.Look(session), Actions = _rig.ActionsSent().Count }.Should().BeEquivalentTo(
            new { Session = before, Actions = 0 },
            $"a dialing call younger than the dialing timeout is not a candidate. Measured: {_rig.Describe()}");
        session.State.Should().Be(CallSessionState.Dialing);
    }

    [Fact]
    public async Task StopAsync_ShouldStopGracefully()
    {
        var loop = await _rig.StartLoopAsync(_rig.BuildSweep());

        var error = await Record.ExceptionAsync(loop.StopAsync);

        error.Should().BeNull("a stop of a started sweep cancels its loop and returns inside the bound");
    }
}
