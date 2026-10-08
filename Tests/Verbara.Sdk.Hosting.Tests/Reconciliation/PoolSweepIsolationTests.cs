using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// One server whose verification throws, hangs, is not connected or leaves the pool under the sweep never stops the
/// verification of the others, nor the next tick; the pool and the servers it holds may change between ticks.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class PoolSweepIsolationTests : IAsyncLifetime
{
    private readonly PoolSweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    private bool EndedAsAReload(CallSession call) =>
        call.State == CallSessionState.Completed && call.Metadata.GetValueOrDefault("cause") == "reload"
        && call.HangupCause is null && _rig.EndingsOf(call) == 1;

    private (RigServer First, RigServer Second) TwoServersInPoolOrder()
    {
        _rig.AddServer("a");
        _rig.AddServer("b");
        return (_rig[_rig.PoolOrder[0]], _rig[_rig.PoolOrder[1]]);
    }

    private int ErrorsNaming(string serverId) => _rig.Log.Entries.Count(e =>
        e.Level == LogLevel.Error && e.Values.TryGetValue("ServerId", out var id) && Equals(id, serverId));

    [Fact]
    public async Task PoolSweep_ShouldVerifyTheOtherServerAndKeepTicking_WhenOneServersVerificationThrows()
    {
        var (failing, healthy) = TwoServersInPoolOrder();
        failing.LostCall(_rig.Manager, "on-failing");
        failing.StatusReply = _ => RigServer.Throw(new InvalidOperationException("the Status of the failing server"));
        var first = healthy.LostCall(_rig.Manager, "first");
        healthy.AsteriskLists();
        await _rig.StartAsync();

        var firstTick = await _rig.TickAsync();
        var second = healthy.LostCall(_rig.Manager, "second");
        var secondTick = await _rig.TickAsync();

        new
        {
            firstTick,
            secondTick,
            FirstEnded = EndedAsAReload(first),
            SecondEnded = EndedAsAReload(second),
            ErrorsNamingTheFailingServer = ErrorsNaming(failing.Id),
        }.Should().BeEquivalentTo(
            new { firstTick = true, secondTick = true, FirstEnded = true, SecondEnded = true, ErrorsNamingTheFailingServer = 2 },
            $"server {failing.Id}'s failure is logged with its id on each tick, while server {healthy.Id} is verified on "
            + $"both and the loop goes on. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldSkipAServerThatIsNotConnectedAndVerifyTheOther_ThenVerifyItOnceItIsBack()
    {
        var (down, up) = TwoServersInPoolOrder();
        var onDown = down.LostCall(_rig.Manager, "on-down");
        var onUp = up.LostCall(_rig.Manager, "on-up");
        down.AsteriskLists();
        up.AsteriskLists();
        down.ConnectionState = AmiConnectionState.Reconnecting;
        await _rig.StartAsync();

        await _rig.TickAsync();
        var whileDown = new { DownStatus = down.StatusRequests, DownState = onDown.State, UpEnded = EndedAsAReload(onUp) };
        down.ConnectionState = AmiConnectionState.Connected;
        await _rig.TickAsync();

        new { whileDown, DownEndedOnceBack = EndedAsAReload(onDown) }.Should().BeEquivalentTo(
            new
            {
                whileDown = new { DownStatus = 0, DownState = CallSessionState.Connected, UpEnded = true },
                DownEndedOnceBack = true,
            },
            $"a server that is not connected is skipped, the other verified, and once it is connected a later tick "
            + $"verifies it. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldEndTheOtherServersLostCall_WhileOneServersStatusNeverAnswers()
    {
        var (hanging, healthy) = TwoServersInPoolOrder();
        hanging.LostCall(_rig.Manager, "on-hanging");
        hanging.StatusReply = hanging.Hang;
        var call = healthy.LostCall(_rig.Manager, "on-healthy");
        healthy.AsteriskLists();
        await _rig.StartAsync();

        _rig.Clock.Advance(PoolSweepRig.Interval);
        var ended = await _rig.WaitUntilAsync(() => EndedAsAReload(call));

        new { ended, HangingAsked = hanging.StatusRequests }.Should().BeEquivalentTo(
            new { ended = true, HangingAsked = 1 },
            $"the passes of a tick run side by side: server {hanging.Id}, first in the pool's order, whose Status never "
            + $"answers, does not hold back server {healthy.Id}'s verification. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldVerifyAServerAddedToARunningHost_WhenItsCallLosesItsHangup()
    {
        _rig.AddServer("a");
        await _rig.StartAsync();
        await _rig.TickAsync();

        var added = _rig.AddServer("b");
        var call = added.LostCall(_rig.Manager, "on-added");
        added.AsteriskLists();
        await _rig.TickAsync();

        EndedAsAReload(call).Should().BeTrue(
            $"a tick after the add walks the server the pool holds now. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldNotThrowAndShouldVerifyTheOther_WhenAServerIsRemovedWhileATickHoldsIt()
    {
        _rig.PoolSweep.Should().NotBeNull("the multi-server registration registers the pool sweep");
        var removed = _rig.AddServer("a");
        var healthy = _rig.AddServer("b");
        var left = removed.LostCall(_rig.Manager, "on-removed");
        removed.StatusReply = removed.Hang;
        var call = healthy.LostCall(_rig.Manager, "on-healthy");
        healthy.AsteriskLists();
        await _rig.StartAsync();

        var ticks = _rig.Ticks;
        _rig.Clock.Advance(PoolSweepRig.Interval);
        var held = await _rig.WaitUntilAsync(() => removed.StatusRequests == 1 && EndedAsAReload(call));
        _rig.Manager.DetachFromServer("a");
        await _rig.Pool!.RemoveServerAsync("a").AsTask().WaitAsync(SweepRig.Bound);
        var tickCompleted = await _rig.WaitUntilAsync(() => _rig.Ticks > ticks);
        var nextTick = await _rig.TickAsync();

        new
        {
            held,
            tickCompleted,
            nextTick,
            RemovedAsked = removed.StatusRequests,
            LeftState = left.State,
            Errors = _rig.Log.Entries.Count(e => e.Level >= LogLevel.Error),
            Serverless = _rig.LastServerless,
        }.Should().BeEquivalentTo(
            new
            {
                held = true, tickCompleted = true, nextTick = true, RemovedAsked = 1,
                LeftState = CallSessionState.Connected, Errors = 0, Serverless = (int?)1,
            },
            "a server removed under the tick ends nothing, is not an error once it left the pool, and the next tick "
            + $"neither asks it nor ends its held session, which it counts. Measured: {_rig.Describe()}");
    }
}
