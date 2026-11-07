using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class D5LeftCallTests
{
    [Fact]
    public async Task AttachToServer_ShouldEndAtAttachAndClearOnlyItsIndexes_WhenTheCompleteTableLostTheCallWhileDetached()
    {
        await using var rig = new D5CallRig();
        var a = rig.Server("a"); var b = rig.Server("b");
        await UnjoinedLegBaseline.ReconcileAsync(a);
        rig.Manager.AttachToServer(a.Server, "a"); rig.Manager.AttachToServer(b.Server, "b");
        var call = rig.Open(a, "X", "Y"); var other = rig.Open(b, "X", "Y");
        foreach (var server in new[] { a, b })
        {
            server.Server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null);
            server.Server.Bridges.OnChannelEntered("B", "X");
            server.Server.Bridges.OnChannelEntered("B", "Y");
        }
        var beforeOther = SweepRig.Look(other);
        var saved = rig.Store.SavesOf(call);
        rig.Manager.DetachFromServer("a");
        a.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        a.Server.Channels.OnHangup("Y", HangupCause.NormalClearing);
        var requests = a.StatusRequests;
        var markerInHandler = false;
        using var observer = rig.Manager.Events.OfType<CallEndedEvent>().Subscribe(e =>
        {
            if (e.SessionId == call.SessionId)
                markerInHandler = rig.Manager.GetById(e.SessionId)?.Metadata.GetValueOrDefault("cause") == "reload";
        });
        rig.Manager.AttachToServer(a.Server, "a");
        rig.Ends(call).Should().Be(1);
        markerInHandler.Should().BeTrue();
        call.HangupCause.Should().BeNull();
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload").Should().Be(2);
        rig.Manager.GetByChannelId("a", "X").Should().BeNull();
        rig.Manager.GetByChannelId("a", "Y").Should().BeNull();
        rig.Manager.GetByBridgeId("a", "B").Should().BeNull();
        rig.Manager.InspectIndexes().Bridges.Should().NotContain(e => e.Value == call.SessionId);
        rig.Manager.GetByChannelId("b", "X").Should().BeSameAs(other);
        rig.Manager.GetByChannelId("b", "Y").Should().BeSameAs(other);
        rig.Manager.GetByBridgeId("b", "B").Should().BeSameAs(other);
        SweepRig.Look(other).Should().BeEquivalentTo(beforeOther);
        rig.Store.SavesOf(call).Should().BeGreaterThan(saved);
        a.StatusRequests.Should().Be(requests);
        saved = rig.Store.SavesOf(call);
        rig.Manager.AttachToServer(a.Server, "a");
        rig.Ends(call).Should().Be(1);
        rig.Store.SavesOf(call).Should().Be(saved);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task AttachToServer_ShouldObserveTheHangupBeforeItsDecision_WhenTheSubscriptionsAreAlreadyInstalled()
    {
        await using var rig = new D5CallRig();
        var server = rig.Server("a");
        rig.Manager.AttachToServer(server.Server, "a");
        var call = rig.Open(server, "Y");
        rig.Manager.DetachFromServer("a");
        await UnjoinedLegBaseline.ReconcileAsync(server, "Y");
        var asks = server.StatusRequests;
        using var ready = StepParker.Install(rig.Manager, HandlerStep.AttachmentReadyToDecide, "a", string.Empty);
        var attach = Task.Run(() => rig.Manager.AttachToServer(server.Server, "a"));
        try
        {
            ready.WaitUntilParked();
            server.Server.Channels.OnHangup("Y", HangupCause.NormalClearing);
            rig.Ends(call).Should().Be(1, "the installed current attachment must record the hangup before iii runs");
            call.HangupCause.Should().Be(HangupCause.NormalClearing);
            call.Metadata.Should().NotContainKey("cause");
        }
        finally { ready.LetGo(); await attach.WaitAsync(SweepRig.Bound); }
        rig.Ends(call).Should().Be(1);
        server.StatusRequests.Should().Be(asks);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciliation_ShouldKeepTheSurvivorsCause_WhenItsHangupRacesTheFirstCompleteDecision(bool hangupFirst)
    {
        await using var rig = new D5CallRig();
        var server = rig.Server("a");
        rig.Manager.AttachToServer(server.Server, "a");
        var call = rig.Open(server, "X", "Y");
        rig.Manager.DetachFromServer("a");
        server.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        rig.Manager.AttachToServer(server.Server, "a");
        using var decision = StepParker.Install(rig.Manager, HandlerStep.DecisionOracleCaptured, "a", call.SessionId);
        using var removal = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", "Y");
        var workers = new List<Task>();
        var saved = rig.Store.SavesOf(call);
        try
        {
            if (hangupFirst)
            {
                workers.Add(Task.Run(() => server.Server.Channels.OnHangup("Y", HangupCause.NormalClearing)));
                removal.WaitUntilParked();
                workers.Add(Task.Run(() => UnjoinedLegBaseline.ReconcileAsync(server)));
            }
            else
            {
                workers.Add(Task.Run(() => UnjoinedLegBaseline.ReconcileAsync(server, "Y")));
                decision.WaitUntilParked();
                workers.Add(Task.Run(() => server.Server.Channels.OnHangup("Y", HangupCause.NormalClearing)));
                removal.WaitUntilParked();
            }
            decision.WaitUntilParked();
            decision.LetGo();
            var reconciliation = workers[hangupFirst ? 1 : 0];
            await reconciliation.WaitAsync(SweepRig.Bound);
            call.Participants.Single(p => p.UniqueId == "X").LeftAt.Should().NotBeNull();
            call.Participants.Single(p => p.UniqueId == "X").HangupCause.Should().BeNull();
            call.Participants.Single(p => p.UniqueId == "Y").LeftAt.Should().BeNull("Y is still being announced, so retirement cannot steal its cause");
            rig.Manager.GetByChannelId("a", "X").Should().BeNull();
            rig.Manager.GetByChannelId("a", "Y").Should().BeSameAs(call);
            rig.Ends(call).Should().Be(0);
            rig.Store.SavesOf(call).Should().BeGreaterThan(saved);
        }
        finally
        {
            decision.LetGo(); removal.LetGo();
            await Task.WhenAll(workers).WaitAsync(SweepRig.Bound);
        }
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload").Should().Be(1);
        rig.Manager.InspectIndexes().ChannelIds.Should().NotContain(e => ReferenceEquals(e.Value, call));
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task SnapshotCompletion_ShouldUseTheAppliedTable_WhenTheReadStartedBeforeReattachAndTheRawRowsStillListTheGoneLeg()
    {
        await using var rig = new D5CallRig();
        var server = rig.Server("a");
        rig.Manager.AttachToServer(server.Server, "a");
        var call = rig.Open(server, "X");
        rig.Manager.DetachFromServer("a");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.StatusReply = token => SuspendedStatus(opened, answer, token);
        var read = server.Server.ReconcileChannelsAsync().AsTask();
        try
        {
            await opened.Task.WaitAsync(SweepRig.Bound);
            server.Server.Channels.OnHangup("X", HangupCause.UserBusy);
            rig.Manager.AttachToServer(server.Server, "a");
            rig.Ends(call).Should().Be(0);
            answer.TrySetResult();
            await read.WaitAsync(SweepRig.Bound);
            server.Server.Channels.GetByUniqueId("X").Should().BeNull("the applied read window excludes the real in-read hangup despite its raw Status row");
            rig.Ends(call).Should().Be(1);
            call.Metadata["cause"].Should().Be("reload");
            call.HangupCause.Should().BeNull();
        }
        finally { answer.TrySetResult(); await read.WaitAsync(SweepRig.Bound); }
        SessionIndexInvariant.Assert(rig.Manager);
    }

    private static async IAsyncEnumerable<ManagerEvent> SuspendedStatus(TaskCompletionSource opened, TaskCompletionSource answer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        opened.TrySetResult();
        await answer.Task.WaitAsync(cancellationToken);
        yield return new StatusEvent
        {
            EventType = "Status", UniqueId = "X", Channel = "PJSIP/X", LinkedId = "L",
            RawFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["Channel"] = "PJSIP/X", ["Uniqueid"] = "X", ["Linkedid"] = "L", ["ChannelState"] = "6", ["ChannelStateDesc"] = "Up" }
        };
    }
}

internal sealed class D5CallRig : IAsyncDisposable
{
    private readonly PoolSweepRig _pool = new();
    public D5SaveStore Store { get; } = new();
    public CallSessionManager Manager { get; }
    private readonly ConcurrentQueue<SessionDomainEvent> _events = new();
    private readonly IDisposable _subscription;
    public D5CallRig()
    {
        Manager = new CallSessionManager(Options.Create(_pool.Options), NullLogger<CallSessionManager>.Instance, Store, _pool.Clock);
        _subscription = Manager.Events.Subscribe(_events.Enqueue);
    }
    public RigServer Server(string id) => _pool.AddServer(id, attach: false);
    public CallSession Open(RigServer server, params string[] ids)
    {
        foreach (var id in ids) server.Server.Channels.OnNewChannel(id, "PJSIP/" + id, ChannelState.Up, linkedId: "L");
        return Manager.GetByLinkedId(server.Id, "L")!;
    }
    public int Ends(CallSession call) => _events.OfType<CallEndedEvent>().Count(e => e.SessionId == call.SessionId);
    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await Manager.DisposeAsync();
        await _pool.DisposeAsync();
    }
}

internal sealed class D5SaveStore : SessionStoreBase
{
    public ConcurrentQueue<(string Id, int Present)> Saves { get; } = new();
    public int SavesOf(CallSession call) => Saves.Count(entry => entry.Id == call.SessionId);
    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        Saves.Enqueue((session.SessionId, session.Participants.Count(p => !p.LeftAt.HasValue)));
        return ValueTask.CompletedTask;
    }
    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
}
