using System.Collections.Concurrent;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class ScopedSweepDecisionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sweep_ShouldKeepCountingTheObservedCall_WhenItsRemovalHasNotReachedItsLock(bool singleServer, bool ordinaryCandidate)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a");
        server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        call.Unverified.Should().BeFalse();
        rig.Manager.IsPendingDecision(call).Should().BeFalse("the current attachment really observed this call");
        if (ordinaryCandidate)
        {
            server.LostCall(rig.Manager, "ordinary");
            server.AsteriskLists("ordinary");
        }
        using var parked = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", "X");
        var removal = Task.Run(() => server.Server.Channels.OnHangup("X", HangupCause.NormalClearing));
        try
        {
            parked.WaitUntilParked();
            server.Server.Channels.GetByUniqueId("X").Should().BeNull();
            rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
            await Pass(rig, server.Server, singleServer ? null : "a");
            Count(rig, "Unverifiable").Should().Be(1);
            server.StatusRequests.Should().Be(ordinaryCandidate ? 1 : 0);
            rig.EndingsOf(call).Should().Be(0);
            call.Participants.Single().LeftAt.Should().BeNull();
            call.Unverified.Should().BeFalse();
        }
        finally { parked.LetGo(); await removal.WaitAsync(SweepRig.Bound); }
        rig.EndingsOf(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
        await Pass(rig, server.Server, singleServer ? null : "a");
        Count(rig, "Unverifiable").Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldRequestOneStatus_WhenAYoungReconstructedCallWaitsOnAnIncompleteTable(bool singleServer)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a");
        var call = new CallSession("restored", "L", "a", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        Complete(server.Server).Should().BeFalse();
        await Pass(rig, server.Server, singleServer ? null : "a");
        server.StatusRequests.Should().Be(1, "an incomplete pending call supplies an age-independent Status candidate");
        rig.EndingsOf(call).Should().Be(1);
        call.Metadata["cause"].Should().Be("reload");
        Count(rig, "Unverifiable").Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Sweep_ShouldDecideWithoutStatus_WhenABusyAttachLeftAPendingCallOnACompleteTable(bool singleServer, bool disconnected, bool unreported)
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a");
        await first.Server.ReconcileChannelsAsync();
        first.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        call.State = CallSessionState.Connected;
        await using var replacement = new VerbaraServer(first.Server.Connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<VerbaraServer>.Instance);
        await replacement.ReconcileChannelsAsync();
        using var parked = new SweepDecisionGate();
        using var subscription = rig.Manager.Events.OfType<CallHeldEvent>().Subscribe(_ => parked.Hold());
        var held = Task.Run(() => first.Server.Channels.OnHold("X", null));
        try
        {
            await parked.Entered.Task.WaitAsync(SweepRig.Bound);
            rig.Manager.DetachFromServer("a");
            await Task.Run(() => rig.Manager.AttachToServer(replacement, "a")).WaitAsync(SweepRig.Bound);
            rig.Log.Entries.Count(e => e.Level == LogLevel.Error && e.Values.TryGetValue("SessionId", out var id)
                && Equals(id, call.SessionId)).Should().Be(1);
        }
        finally { parked.Release(); await held.WaitAsync(SweepRig.Bound); }
        rig.Manager.IsPendingDecision(call).Should().BeTrue();
        if (disconnected) first.ConnectionState = AmiConnectionState.Disconnected;
        if (unreported)
        {
            replacement.Channels.OnNewChannel("ordinary", "PJSIP/ordinary", ChannelState.Up, linkedId: "ordinary-L");
            var ordinary = rig.Manager.GetByLinkedId("a", "ordinary-L")!;
            SweepRig.CreatedAtField.SetValue(ordinary, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
            first.Server.Connection.ReportsEventActionOutcome.Returns(false);
        }
        var asked = first.StatusRequests;
        var markerInsideEnding = false;
        using var endings = rig.Manager.Events.OfType<CallEndedEvent>().Subscribe(e =>
        {
            if (e.SessionId == call.SessionId) markerInsideEnding = call.Metadata.GetValueOrDefault("cause") == "reload";
        });
        await Pass(rig, replacement, singleServer ? null : "a");
        rig.EndingsOf(call).Should().Be(1, "the actual sweep must run its Status-free decision before skips or counting");
        markerInsideEnding.Should().BeTrue();
        call.HangupCause.Should().BeNull();
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload").Should().Be(1);
        rig.Manager.GetByChannelId("a", "X").Should().BeNull();
        first.StatusRequests.Should().Be(asked);
        Count(rig, "Unverifiable").Should().Be(0);
        await Pass(rig, replacement, singleServer ? null : "a");
        rig.EndingsOf(call).Should().Be(1);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task SingleServerSweep_ShouldIgnoreTheOtherAttachedServer_WhenItsChannelIdsCollide()
    {
        await using var rig = new PoolSweepRig();
        var a = rig.AddServer("a");
        var b = rig.AddServer("b");
        var call = b.LostCall(rig.Manager, "same");
        foreach (var id in b.UniqueIdsOf("same")) a.ChannelWithoutSession(id);
        var before = SweepRig.Look(call);
        using var factory = LoggerFactory.Create(b => b.AddProvider(rig.Log));
        using var service = new SessionReconciliationService(rig.Manager, a.Server, Options.Create(rig.Options),
            factory.CreateLogger<SessionReconciliationService>(), rig.Clock);
        await service.SweepAsync(CancellationToken.None);
        a.StatusRequests.Should().Be(0, "B's participants must not cause A's single-server service to request Status");
        SweepRig.Look(call).Should().BeEquivalentTo(before);
        rig.EndingsOf(call).Should().Be(0);
    }

    [Fact]
    public async Task Sweep_ShouldSelectLeftCalls_WhenOnlyTheirGenerationShowsTheyArePending()
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a");
        var calls = new List<CallSession>();
        for (var i = 0; i < 10; i++)
        {
            first.Server.Channels.OnNewChannel("X" + i, "PJSIP/X" + i, ChannelState.Up, linkedId: "L" + i);
            calls.Add(rig.Manager.GetByLinkedId("a", "L" + i)!);
        }
        rig.Manager.DetachFromServer("a");
        await using var replacement = new VerbaraServer(first.Server.Connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<VerbaraServer>.Instance);
        rig.Manager.AttachToServer(replacement, "a");
        calls.Should().OnlyContain(c => !c.Unverified, "an incomplete attach must leave lazy demotion unapplied");
        await Pass(rig, replacement, "a");
        first.StatusRequests.Should().Be(1);
        calls.Should().OnlyContain(c => c.EndingDelivered);
        rig.Endings.Should().HaveCount(10);
        Count(rig, "Unverifiable").Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task Sweep_ShouldCountTheYoungWaitingCallOnEveryTick_WhenActualStatusIsRefused()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.ConnectAsync(peer);
        await using var rig = new PoolSweepRig();
        rig.Manager.AttachToServer(run.Server, "a");
        var call = new CallSession("refused", "L", "a", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        await run.Server.ReconcileChannelsAsync();
        run.Server.LastStatusRefusal.Should().NotBeNull();
        await Pass(rig, run.Server, "a");
        Count(rig, "Unverifiable").Should().Be(1, "real refusal state makes even this young waiting call visible");
        await Pass(rig, run.Server, "a");
        Count(rig, "Unverifiable").Should().Be(1);
        rig.EndingsOf(call).Should().Be(0);
        peer.StatusRefusedFromAsk = 0;
        await Pass(rig, run.Server, "a");
        run.Server.LastStatusRefusal.Should().BeNull();
        rig.EndingsOf(call).Should().Be(1);
        await Pass(rig, run.Server, "a");
        Count(rig, "Unverifiable").Should().Be(0);
        peer.Fault.Should().BeNull();
    }

    internal static bool Complete(VerbaraServer server) =>
        (bool)typeof(VerbaraServer).GetProperty("ChannelTableComplete", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;

    internal static async Task Pass(PoolSweepRig rig, VerbaraServer server, string? id)
    {
        if (id is not null)
        {
            await new ServerSweep(rig.Manager, rig.Options, rig.Log.CreateLogger("ScopedSweep"), rig.Clock)
                .SweepAsync(server, id, CancellationToken.None).WaitAsync(SweepRig.Bound);
            return;
        }
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(rig.Log));
        using var service = new SessionReconciliationService(rig.Manager, server, Options.Create(rig.Options),
            factory.CreateLogger<SessionReconciliationService>(), rig.Clock);
        await service.SweepAsync(CancellationToken.None).WaitAsync(SweepRig.Bound);
    }

    internal static int Count(PoolSweepRig rig, string key) =>
        (int)rig.Log.Entries.Last(e => e.Values.ContainsKey(key)).Values[key]!;
}

internal sealed class SweepDecisionGate : IDisposable
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();
    public void Hold()
    {
        Entered.TrySetResult();
        // fence-allow: GUARD-TIMEOUT — bounds a causal notification hold; the test explicitly releases it
        if (!_release.Wait(SweepRig.Bound)) throw new TimeoutException("sweep decision notification was not released");
    }
    public void Release() => _release.Set();
    public void Dispose() => _release.Dispose();
}
