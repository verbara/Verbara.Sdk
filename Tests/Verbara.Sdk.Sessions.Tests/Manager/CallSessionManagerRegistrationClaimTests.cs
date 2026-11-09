using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;
using Xunit;

namespace Verbara.Sdk.Sessions.Tests.Manager;

// Exercises registration claims through per-call locks and the public live event flow.
public sealed class CallSessionManagerRegistrationClaimTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RegisterReconstructedSession_ShouldRefuseAndLogTheOwner_WhenAnotherCallMapsItsLeg()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var original = Call("original", "L1", "a", "X");
        original.BridgeId = "kept-bridge";
        rig.Manager.RegisterReconstructedSession(original).Should().BeTrue();
        var before = rig.Manager.InspectIndexes();
        var saves = store.Saves.Length;
        var notifications = rig.Events.Count;
        var refused = Call("refused", "L2", "a", "X");
        refused.BridgeId = "refused-bridge";

        var accepted = rig.Manager.RegisterReconstructedSession(refused);

        accepted.Should().BeFalse("a's X already belongs to the held original instance");
        rig.Manager.InspectIndexes().Should().BeEquivalentTo(before);
        rig.Manager.GetByChannelId("a", "X").Should().BeSameAs(original);
        rig.Manager.GetByBridgeId("a", "kept-bridge").Should().BeSameAs(original);
        AssertNoEntry(rig.Manager, refused);
        AssertLegInformation(rig, refused, "X", original);
        store.Saves.Should().HaveCount(saves);
        rig.Events.Should().HaveCount(notifications);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RegisterReconstructedSession_ShouldRefuseWithoutAnEntry_WhenItsLinkedIdIsNullOrEmpty(string? linkedId)
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var original = Call("original", "L", "a", "held");
        rig.Manager.RegisterReconstructedSession(original).Should().BeTrue();
        var before = rig.Manager.InspectIndexes();
        var saves = store.Saves.Length;
        var notifications = rig.Events.Count;
        var refused = Call("invalid", linkedId!, "a", "new-X");
        refused.BridgeId = "invalid-bridge";

        // Calling directly keeps a null-key exception visible as a runtime failure on the unfixed method.
        var accepted = rig.Manager.RegisterReconstructedSession(refused);

        accepted.Should().BeFalse();
        rig.Manager.InspectIndexes().Should().BeEquivalentTo(before);
        AssertNoEntry(rig.Manager, refused);
        var log = AssertInformation(rig, refused, "LogRegistrationRefused");
        log.Values["Reason"].Should().Be("linkedid is null or empty");
        store.Saves.Should().HaveCount(saves);
        rig.Events.Should().HaveCount(notifications);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldRefuseAndLogBothCalls_WhenItsOwnServerHoldsTheLinkedId()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var original = Call("original", "L", "a", "X");
        var refused = Call("refused", "L", "a", "Y");
        rig.Manager.RegisterReconstructedSession(original).Should().BeTrue();
        var before = rig.Manager.InspectIndexes();
        var saves = store.Saves.Length;
        var notifications = rig.Events.Count;

        rig.Manager.RegisterReconstructedSession(refused).Should().BeFalse();

        rig.Manager.InspectIndexes().Should().BeEquivalentTo(before);
        rig.Manager.GetByLinkedId("a", "L").Should().BeSameAs(original);
        AssertNoEntry(rig.Manager, refused);
        var log = AssertInformation(rig, refused, "LogRegistrationRefused");
        log.Values["Reason"].Should().Be("linkedid is already held on this server");
        log.Values["HolderSessionId"].Should().Be(original.SessionId);
        store.Saves.Should().HaveCount(saves);
        rig.Events.Should().HaveCount(notifications);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldAcceptBothServers_WhenOnlyTheirExternalIdsCoincide()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var a = Call("a-call", "L", "a", "X");
        var b = Call("b-call", "L", "b", "X");
        a.BridgeId = b.BridgeId = "bridge";

        rig.Manager.RegisterReconstructedSession(a).Should().BeTrue();
        rig.Manager.RegisterReconstructedSession(b).Should().BeTrue();

        rig.Manager.GetById(a.SessionId).Should().BeSameAs(a);
        rig.Manager.GetById(b.SessionId).Should().BeSameAs(b);
        rig.Manager.GetByLinkedId("a", "L").Should().BeSameAs(a);
        rig.Manager.GetByLinkedId("b", "L").Should().BeSameAs(b);
        rig.Manager.GetByChannelId("a", "X").Should().BeSameAs(a);
        rig.Manager.GetByChannelId("b", "X").Should().BeSameAs(b);
        rig.Manager.GetByBridgeId("a", "bridge").Should().BeSameAs(a);
        rig.Manager.GetByBridgeId("b", "bridge").Should().BeSameAs(b);
        rig.Manager.GetByLinkedId("L").Should().BeNull();
        rig.Manager.GetByChannelId("X").Should().BeNull();
        rig.Manager.GetByBridgeId("bridge").Should().BeNull();
        store.Saves.Should().BeEquivalentTo(new[] { a, b });
        rig.Events.Should().BeEmpty();
        rig.Log.At(LogLevel.Information).Should().BeEmpty();
        rig.Log.At(LogLevel.Debug).Should().BeEmpty();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task RegisterReconstructedSession_ShouldRefuseAndLog_WhenTheGlobalSessionIdIsAlreadyHeld(string serverId)
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var original = Call("held-id", "L1", "a", "X");
        rig.Manager.RegisterReconstructedSession(original).Should().BeTrue();
        var refused = Call(original.SessionId, "L2", serverId, "Y");
        var before = rig.Manager.InspectIndexes();
        var saves = store.Saves.Length;
        var notifications = rig.Events.Count;

        rig.Manager.RegisterReconstructedSession(refused).Should().BeFalse();

        // A new empty server-index container may be lazily allocated by IndexOf; compare actual entries here.
        var after = rig.Manager.InspectIndexes();
        after.Sessions.Should().BeEquivalentTo(before.Sessions);
        after.LinkedIds.Should().BeEquivalentTo(before.LinkedIds);
        after.ChannelIds.Should().BeEquivalentTo(before.ChannelIds);
        after.Bridges.Should().BeEquivalentTo(before.Bridges);
        rig.Manager.GetById(original.SessionId).Should().BeSameAs(original);
        rig.Manager.GetByLinkedId(serverId, "L2").Should().BeNull();
        AssertNoEntry(rig.Manager, refused);
        var log = AssertInformation(rig, refused, "LogRegistrationRefused");
        log.Values["Reason"].Should().Be("session id is already held by another instance");
        log.Values["HolderSessionId"].Should().Be(original.SessionId);
        store.Saves.Should().HaveCount(saves);
        rig.Events.Should().HaveCount(notifications);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldChangeNothing_WhenTheSameInstanceIsRegisteredAgain()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var call = Call("held", "L", "a", "X");
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var before = rig.Manager.InspectIndexes();
        var saves = store.Saves.Length;
        var notifications = rig.Events.Count;

        rig.Manager.RegisterReconstructedSession(call).Should().BeFalse();

        rig.Manager.InspectIndexes().Should().BeEquivalentTo(before);
        store.Saves.Should().HaveCount(saves);
        rig.Events.Should().HaveCount(notifications);
        var log = AssertInformation(rig, call, "LogRegistrationRefused");
        log.Values["Reason"].Should().Be("the same instance is already registered");
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldKeepAndDebugTheBridgeOwner_WhenOnlyItsBridgeIsAlreadyMapped()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var original = Call("original", "L1", "a", "X");
        var next = Call("next", "L2", "a", "Y");
        original.BridgeId = next.BridgeId = "shared-bridge";
        rig.Manager.RegisterReconstructedSession(original).Should().BeTrue();

        rig.Manager.RegisterReconstructedSession(next).Should().BeTrue();

        rig.Manager.GetByBridgeId("a", "shared-bridge").Should().BeSameAs(original);
        rig.Manager.GetByLinkedId("a", "L2").Should().BeSameAs(next);
        rig.Manager.GetByChannelId("a", "Y").Should().BeSameAs(next);
        var log = rig.Log.At(LogLevel.Debug).Should().ContainSingle().Which;
        log.EventName.Should().Be("LogRegistrationBridgeSkipped");
        log.Values["ServerId"].Should().Be("a");
        log.Values["LinkedId"].Should().Be(next.LinkedId);
        log.Values["SessionId"].Should().Be(next.SessionId);
        log.Values["BridgeId"].Should().Be("shared-bridge");
        log.Values["HolderSessionId"].Should().Be(original.SessionId);
        rig.Log.At(LogLevel.Information).Should().BeEmpty();
        rig.Events.Should().BeEmpty();
        store.Saves.Should().BeEquivalentTo(new[] { original, next });
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldAcceptOneCall_WhenBothPassedTheirSharedLegPrecheck()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var first = Call("first", "L1", "a", "X");
        var second = Call("second", "L2", "a", "X");
        var workers = new RegistrationWorkers();
        var firstGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", first.SessionId);
        var secondGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", second.SessionId);
        bool[] accepted = [];
        await workers.RunAsync(async () =>
        {
            var one = workers.Start(() => rig.Manager.RegisterReconstructedSession(first));
            var two = workers.Start(() => rig.Manager.RegisterReconstructedSession(second));
            firstGate.WaitUntilParked(Bound);
            secondGate.WaitUntilParked(Bound);
            firstGate.HasParked.Should().BeTrue();
            secondGate.HasParked.Should().BeTrue();
            rig.Manager.GetByChannelId("a", "X").Should().BeNull("both reached the real preclaim boundary while X was unowned");
            firstGate.LetGo();
            secondGate.LetGo();
            // fence-allow: GUARD-TIMEOUT — await both actual registration producers, not a settling delay.
            accepted = await Task.WhenAll(one, two).WaitAsync(Bound);
        });

        accepted.Count(value => value).Should().Be(1, "the actual channel TryAdd arbitrates the winner");
        var winner = accepted[0] ? first : second;
        var loser = accepted[0] ? second : first;
        rig.Manager.GetByChannelId("a", "X").Should().BeSameAs(winner);
        rig.Manager.GetByLinkedId("a", winner.LinkedId).Should().BeSameAs(winner);
        AssertNoEntry(rig.Manager, loser);
        AssertLegInformation(rig, loser, "X", winner);
        store.Saves.Should().ContainSingle().Which.Should().BeSameAs(winner);
        loser.Events.Should().BeEmpty();
        rig.Events.Should().BeEmpty();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldWithdrawItsPrivateClaim_WhenItLosesTheNextSharedLeg()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var protectedCall = Call("protected", "protected-L", "a", "protected-X");
        protectedCall.BridgeId = "protected-bridge";
        rig.Manager.RegisterReconstructedSession(protectedCall).Should().BeTrue();
        var winner = Call("winner", "winner-L", "a", "X");
        winner.BridgeId = "winner-bridge";
        var loser = Call("loser", "loser-L", "a", "P", "X");
        loser.BridgeId = "loser-bridge";
        var workers = new RegistrationWorkers();
        var winnerGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", winner.SessionId);
        var loserGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", loser.SessionId);
        var privateGate = workers.Park(rig.Manager, HandlerStep.RegistrationLegClaimed, "a", "P");
        var privateClaimedByLoser = false;
        var bridgeAbsentAtPrivateClaim = false;
        var winnerAccepted = false;
        var loserAccepted = true;
        await workers.RunAsync(async () =>
        {
            var one = workers.Start(() => rig.Manager.RegisterReconstructedSession(winner));
            var two = workers.Start(() => rig.Manager.RegisterReconstructedSession(loser));
            winnerGate.WaitUntilParked(Bound);
            loserGate.WaitUntilParked(Bound);
            rig.Manager.GetByChannelId("a", "X").Should().BeNull("both real prechecks precede the first ownership claim");
            loserGate.LetGo();
            privateGate.WaitUntilParked(Bound);
            privateClaimedByLoser = ReferenceEquals(rig.Manager.GetByChannelId("a", "P"), loser);
            bridgeAbsentAtPrivateClaim = rig.Manager.GetByBridgeId("a", loser.BridgeId!) is null;
            winnerGate.LetGo();
            // fence-allow: GUARD-TIMEOUT — wait for the real winning claim before releasing the loser's P boundary.
            winnerAccepted = await one.WaitAsync(Bound);
            privateGate.LetGo();
            // fence-allow: GUARD-TIMEOUT — observe this registration's own completed rollback.
            loserAccepted = await two.WaitAsync(Bound);
        });

        privateClaimedByLoser.Should().BeTrue("the loser owned P before its atomic claim of X failed");
        bridgeAbsentAtPrivateClaim.Should().BeTrue("no bridge is published before all leg claims succeed");
        winnerAccepted.Should().BeTrue();
        loserAccepted.Should().BeFalse();
        rig.Manager.GetByChannelId("a", "P").Should().BeNull();
        rig.Manager.GetByChannelId("a", "X").Should().BeSameAs(winner);
        rig.Manager.GetByLinkedId("a", winner.LinkedId).Should().BeSameAs(winner);
        rig.Manager.GetByBridgeId("a", "winner-bridge").Should().BeSameAs(winner);
        rig.Manager.GetByBridgeId("a", "protected-bridge").Should().BeSameAs(protectedCall);
        rig.Manager.GetByChannelId("a", "protected-X").Should().BeSameAs(protectedCall);
        AssertNoEntry(rig.Manager, loser);
        AssertLegInformation(rig, loser, "X", winner);
        store.Saves.Should().BeEquivalentTo(new[] { protectedCall, winner });
        loser.Events.Should().BeEmpty();
        rig.Events.Should().BeEmpty();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task OnChannelAdded_ShouldRetryTheHeldOwner_WhenItsCapturedRegistrationWasRolledBack()
    {
        var store = new RegistrationRecordingStore();
        await using var rig = await ReleaseRig.StartAsync(store: store);
        var server = await rig.NewInstanceAsync("a");
        server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "table-only-L");
        rig.Manager.AttachToServer(server.Server, "a");
        rig.Current(server);
        rig.Manager.GetByChannelId("a", "X").Should().BeNull("loading before attach creates no engine call");
        var winner = Call("reader-winner", "winner-L", "a", "X");
        var loser = Call("reader-loser", "loser-L", "a", "P", "X");
        loser.BridgeId = "reader-loser-bridge";
        var workers = new RegistrationWorkers();
        var winnerGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", winner.SessionId);
        var loserGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", loser.SessionId);
        var privateGate = workers.Park(rig.Manager, HandlerStep.RegistrationLegClaimed, "a", "P");
        var readerGate = workers.Park(rig.Manager, HandlerStep.JoinFound, "a", "Y");
        var winnerAccepted = false;
        var loserAccepted = true;
        var capturedProvisionalOwner = false;
        var ownPrivateLegAtCapture = false;
        var provisionalRemovedBeforeReader = false;
        await workers.RunAsync(async () =>
        {
            var one = workers.Start(() => rig.Manager.RegisterReconstructedSession(winner));
            var two = workers.Start(() => rig.Manager.RegisterReconstructedSession(loser));
            winnerGate.WaitUntilParked(Bound);
            loserGate.WaitUntilParked(Bound);
            rig.Manager.GetByChannelId("a", "X").Should().BeNull();
            loserGate.LetGo();
            privateGate.WaitUntilParked(Bound);
            ownPrivateLegAtCapture = ReferenceEquals(rig.Manager.GetByChannelId("a", "P"), loser);
            var arrival = workers.Start(() => server.Server.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up,
                context: "from-trunk", linkedId: loser.LinkedId));
            readerGate.WaitUntilParked(Bound);
            capturedProvisionalOwner = ReferenceEquals(rig.Manager.GetByLinkedId("a", loser.LinkedId), loser);
            winnerGate.LetGo();
            // fence-allow: GUARD-TIMEOUT — winning registration's real completion precedes the losing claim.
            winnerAccepted = await one.WaitAsync(Bound);
            privateGate.LetGo();
            // fence-allow: GUARD-TIMEOUT — read rollback only after the actual losing producer returned.
            loserAccepted = await two.WaitAsync(Bound);
            provisionalRemovedBeforeReader = rig.Manager.GetById(loser.SessionId) is null
                && rig.Manager.GetByLinkedId("a", loser.LinkedId) is null;
            readerGate.LetGo();
            // fence-allow: GUARD-TIMEOUT — this live arrival's own outbox drain, not an inferred idle interval.
            await arrival.WaitAsync(Bound);
        });

        capturedProvisionalOwner.Should().BeTrue("JoinFound captured the provisional linkedid before rollback");
        ownPrivateLegAtCapture.Should().BeTrue();
        winnerAccepted.Should().BeTrue();
        loserAccepted.Should().BeFalse();
        provisionalRemovedBeforeReader.Should().BeTrue();
        var held = rig.Manager.GetByChannelId("a", "Y");
        held.Should().NotBeNull();
        held.Should().NotBeSameAs(loser);
        rig.Manager.GetById(held!.SessionId).Should().BeSameAs(held);
        rig.Manager.GetByLinkedId("a", loser.LinkedId).Should().BeSameAs(held);
        held.Participants.Should().ContainSingle(row => row.UniqueId == "Y" && row.LeftAt == null);
        loser.Participants.Should().NotContain(row => row.UniqueId == "Y");
        AssertNoEntry(rig.Manager, loser);
        rig.Manager.GetByChannelId("a", "X").Should().BeSameAs(winner);
        store.Saves.Should().NotContain(call => ReferenceEquals(call, loser));
        rig.Events.Select(item => item.Event).OfType<CallStartedEvent>()
            .Should().ContainSingle().Which.SessionId.Should().Be(held.SessionId);
        rig.Events.Should().NotContain(item => item.Event.SessionId == loser.SessionId);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task RunAsync_ShouldReleaseAndDrainRegistrations_WhenItsBodyThrows()
    {
        await using var rig = await ReleaseRig.StartAsync();
        var workers = new RegistrationWorkers();
        var first = Call("cleanup-one", "cleanup-L1", "a", "cleanup-X");
        var second = Call("cleanup-two", "cleanup-L2", "a", "cleanup-Y");
        var firstGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", first.SessionId);
        var secondGate = workers.Park(rig.Manager, HandlerStep.RegistrationClaimsReady, "a", second.SessionId);
        var error = await Record.ExceptionAsync(() => workers.RunAsync(() =>
        {
            _ = workers.Start(() => rig.Manager.RegisterReconstructedSession(first));
            _ = workers.Start(() => rig.Manager.RegisterReconstructedSession(second));
            firstGate.WaitUntilParked(Bound);
            secondGate.WaitUntilParked(Bound);
            throw new InvalidOperationException("intentional registration body failure");
        }));

        output.WriteLine($"registration cleanup witness: drained={workers.Drained}; producers={workers.ProducerCount}; completed={workers.CompletedCount}");
        using var scope = new AssertionScope();
        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("intentional registration body failure");
        workers.Drained.Should().BeTrue();
        workers.ProducerCount.Should().Be(2);
        workers.CompletedCount.Should().Be(2);
        rig.Manager.GetById(first.SessionId).Should().BeSameAs(first);
        rig.Manager.GetById(second.SessionId).Should().BeSameAs(second);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    // This helper is new infrastructure. Its own source mutation must throw from CleanupAfterDrain,
    // after both registration producers ended; a production mutation cannot qualify this cleanup hook.
    private sealed class RegistrationWorkers
    {
        private readonly List<StepParker> _gates = [];
        private readonly List<Task> _producers = [];

        public bool Drained { get; private set; }
        public int ProducerCount => _producers.Count;
        public int CompletedCount => _producers.Count(task => task.IsCompleted);

        public StepParker Park(CallSessionManager manager, HandlerStep step, string serverId, string id)
        {
            var gate = StepParker.Install(manager, step, serverId, id);
            _gates.Add(gate);
            return gate;
        }

        public Task<T> Start<T>(Func<T> work)
        {
            var task = ReleaseRig.OnThread(work);
            _producers.Add(task);
            return task;
        }

        public Task Start(Action work)
        {
            var task = ReleaseRig.OnThread(work);
            _producers.Add(task);
            return task;
        }

        public async Task RunAsync(Func<Task> body)
        {
            try { await body(); }
            finally
            {
                foreach (var gate in _gates) gate.LetGo();
                try
                {
                    // fence-allow: GUARD-TIMEOUT — release every causal gate and join all registered producers.
                    await Task.WhenAll(_producers).WaitAsync(Bound);
                }
                finally
                {
                    Drained = _producers.All(task => task.IsCompleted);
                    foreach (var gate in _gates) gate.Dispose();
                    if (Drained) CleanupAfterDrain();
                }
            }
        }

        private static void CleanupAfterDrain() { }
    }

    private static CallSession Call(string id, string linkedId, string serverId, params string[] legs)
    {
        var call = new CallSession(id, linkedId, serverId, CallDirection.Inbound);
        foreach (var leg in legs)
            call.AddParticipant(new SessionParticipant { UniqueId = leg, Channel = $"PJSIP/{leg}", Technology = "PJSIP" });
        return call;
    }

    private static void AssertNoEntry(CallSessionManager manager, CallSession refused)
    {
        var snapshot = manager.InspectIndexes();
        snapshot.Sessions.Should().NotContain(entry => ReferenceEquals(entry.Value, refused));
        snapshot.LinkedIds.Should().NotContain(entry => ReferenceEquals(entry.Value, refused));
        snapshot.ChannelIds.Should().NotContain(entry => ReferenceEquals(entry.Value, refused));
        // A held-id refusal can share its id with the winner. A fresh refused id must leave no bridge anywhere.
        if (manager.GetById(refused.SessionId) is null)
            snapshot.Bridges.Should().NotContain(entry => entry.Value == refused.SessionId);
        else if (refused.BridgeId is not null)
            snapshot.Bridges.Should().NotContain(entry => entry.Key == refused.BridgeId && entry.Value == refused.SessionId);
    }

    private static LogRecord AssertInformation(ReleaseRig rig, CallSession call, string eventName)
    {
        var log = rig.Log.At(LogLevel.Information).Should().ContainSingle().Which;
        log.EventName.Should().Be(eventName);
        log.Values["ServerId"].Should().Be(call.ServerId);
        log.Values["LinkedId"].Should().Be(call.LinkedId);
        log.Values["SessionId"].Should().Be(call.SessionId);
        log.Exception.Should().BeNull();
        return log;
    }

    private static void AssertLegInformation(ReleaseRig rig, CallSession refused, string leg, CallSession owner)
    {
        var log = AssertInformation(rig, refused, "LogRegistrationLegRefused");
        log.Values["ChannelId"].Should().Be(leg);
        log.Values["HolderSessionId"].Should().Be(owner.SessionId);
    }

    private sealed class RegistrationRecordingStore : SessionStoreBase
    {
        private readonly ConcurrentQueue<CallSession> _saved = new();
        public CallSession[] Saves => [.. _saved];

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _saved.Enqueue(session);
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult(_saved.FirstOrDefault(call => call.SessionId == sessionId));
    }
}
