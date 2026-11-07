using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class SweepScopeIsolationTests
{
    [Fact]
    public async Task SingleServerSweep_ShouldReturnWhileTheOtherCallIsLocked_WhenThatCallBelongsToAnotherInstance()
    {
        await using var rig = new PoolSweepRig();
        var a = rig.AddServer("a");
        var b = rig.AddServer("b");
        var call = b.LostCall(rig.Manager, "held");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = false;
        using var observer = rig.Manager.Events.OfType<CallHeldEvent>().Subscribe(e =>
        {
            if (e.SessionId != call.SessionId) return;
            entered.TrySetResult();
            // fence-allow: GUARD-TIMEOUT — owns B's causal hold beyond A's completion guard; finally releases it
            if (!release.Wait(SweepRig.Bound * 3)) expired = true;
        });
        var holder = Task.Run(() => b.Server.Channels.OnHold(b.UniqueIdsOf("held")[0], null));
        Task? tick = null;
        using var factory = LoggerFactory.Create(log => log.AddProvider(rig.Log));
        using var service = new SessionReconciliationService(rig.Manager, a.Server, Options.Create(rig.Options),
            factory.CreateLogger<SessionReconciliationService>(), rig.Clock);
        try
        {
            await entered.Task.WaitAsync(SweepRig.Bound);
            tick = Task.Run(() => service.SweepAsync(CancellationToken.None));
            var error = await Record.ExceptionAsync(() => tick.WaitAsync(SweepRig.Bound));
            error.Should().BeNull("A's sweep must return while B's subscriber still owns B's call lock");
            release.IsSet.Should().BeFalse();
            a.StatusRequests.Should().Be(0);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(SweepRig.Bound);
            if (tick is not null) await tick.WaitAsync(SweepRig.Bound);
        }
        expired.Should().BeFalse("cleanup must release the owned holder, never wait for its own timeout");
        rig.EndingsOf(call).Should().Be(0);
        call.Participants.Should().OnlyContain(p => !p.LeftAt.HasValue);
    }
}
