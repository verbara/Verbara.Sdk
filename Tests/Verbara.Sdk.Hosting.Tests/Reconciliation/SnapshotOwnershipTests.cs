using System.Collections.Concurrent;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class SnapshotOwnershipTests(ITestOutputHelper output)
{
    private static readonly TimeSpan _snapshotBound = TimeSpan.FromSeconds(5);
    private static readonly string[] _expectedChannelIds = ["X", "Y"];

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnChannelAdded_ShouldKeepTheChannelOwner_WhenAReturningSnapshotRelabelsALeftCallAfterTheAttach(
        bool otherCall, bool refusedFirstLoad)
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a", inPool: false);
        var starts = new ConcurrentQueue<CallStartedEvent>();
        using var subscription = rig.Manager.Events.OfType<CallStartedEvent>().Subscribe(starts.Enqueue);
        first.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        first.Server.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        call.Participants.Select(p => p.UniqueId).Should().Equal("X", "Y");
        call.Unverified.Should().BeFalse();
        rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusRefusedFromAsk = refusedFirstLoad ? 1 : 0,
            StatusChannels = [new StatusChannel("X", "PJSIP/X", "L-prime"), new StatusChannel("Y", "PJSIP/Y", "L-prime")],
        };
        await using var replacement = await Run.ConnectAsync(peer);
        if (refusedFirstLoad)
        {
            await replacement.StartServerAsync();
            replacement.Server.LastStatusRefusal.Should().NotBeNull();
        }
        ScopedSweepDecisionTests.Complete(replacement.Server).Should().BeFalse("both returning paths attach before a complete channel table");
        replacement.Server.Channels.GetByUniqueId("X").Should().BeNull();
        rig.Manager.AttachToServer(replacement.Server, "a");
        CallSession? other = null;
        if (otherCall)
        {
            replacement.Server.Channels.OnNewChannel("Z", "PJSIP/Z", ChannelState.Up, linkedId: "L-prime");
            other = rig.Manager.GetByChannelId("a", "Z")!;
            other.Should().NotBeNull();
            peer.StatusChannels = [.. peer.StatusChannels, new StatusChannel("Z", "PJSIP/Z", "L-prime")];
        }
        var startsBefore = starts.Count;
        var otherEvents = other?.Events.Count ?? 0;
        var sessionsBefore = rig.Manager.InspectIndexes().Sessions.Count;
        peer.StatusRefusedFromAsk = 0;
        if (refusedFirstLoad)
            await replacement.Server.ReconcileChannelsAsync().AsTask().WaitAsync(_snapshotBound);
        else
            await replacement.StartServerAsync().WaitAsync(_snapshotBound);
        ScopedSweepDecisionTests.Complete(replacement.Server).Should().BeTrue();
        replacement.Server.Channels.GetByUniqueId("X")!.LinkedId.Should().Be("L-prime");
        replacement.Server.Channels.GetByUniqueId("Y")!.LinkedId.Should().Be("L-prime");
        if (otherCall) replacement.Server.Channels.GetByUniqueId("Z").Should().NotBeNull("the actual returning snapshot preserves E's live leg");
        var ownerX = ReferenceEquals(rig.Manager.GetByChannelId("a", "X"), call);
        var ownerY = ReferenceEquals(rig.Manager.GetByChannelId("a", "Y"), call);
        var startsAfter = starts.Count;
        var sessionsAfter = rig.Manager.InspectIndexes().Sessions.Count;
        var otherParticipants = other?.Participants.Select(p => p.UniqueId).ToArray() ?? [];
        var otherEventsAfter = other?.Events.Count ?? 0;
        var held = ReferenceEquals(rig.Manager.GetById(call.SessionId), call);
        var endedAtCompletion = rig.EndingsOf(call);
        var debug = rig.Log.Entries.Where(e => e.Level == LogLevel.Debug
            && e.Values.ContainsKey("OwnerSessionId") && Equals(e.Values.GetValueOrDefault("OwnerSessionId"), call.SessionId)).ToArray();
        replacement.Server.Channels.OnHangup("X", HangupCause.NormalClearing);
        var endedAtX = rig.EndingsOf(call);
        replacement.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
        var endedAtY = rig.EndingsOf(call);
        var causeAtY = call.HangupCause;
        var reloadAtY = call.Metadata.GetValueOrDefault("cause");
        if (otherCall) replacement.Server.Channels.OnHangup("Z", HangupCause.NormalClearing);
        peer.StatusChannels = [];
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        (rig.Clock.GetUtcNow() - call.CreatedAt > rig.Options.DialingTimeout).Should().BeTrue();
        rig.Pool!.AddExistingServer("a", replacement.Server);
        rig.Pool.GetServer("a").Should().BeSameAs(replacement.Server);
        await rig.StartAsync();
        var requests = peer.Asked("Status");
        (await rig.TickAsync()).Should().BeTrue();
        var unverifiable = ScopedSweepDecisionTests.Count(rig, "Unverifiable");
        var pass = rig.Log.Entries.Last(e => e.Values.ContainsKey("Unverifiable"));
        output.WriteLine($"snapshot otherCall={otherCall} refusedFirstLoad={refusedFirstLoad}: ownerX={ownerX} ownerY={ownerY}; Starts={startsBefore}->{startsAfter}; sessions={sessionsBefore}->{sessionsAfter}; E=[{string.Join(',', otherParticipants)}]; Eevents={otherEvents}->{otherEventsAfter}; Cendings=completion:{endedAtCompletion},X:{endedAtX},Y:{endedAtY}; unverifiable={unverifiable}; StatusDelta={peer.Asked("Status") - requests}");
        using var assertions = new AssertionScope();
        ownerX.Should().BeTrue("the returning snapshot must preserve X's owner C");
        ownerY.Should().BeTrue("the returning snapshot must preserve Y's owner C");
        startsAfter.Should().Be(startsBefore);
        sessionsAfter.Should().Be(sessionsBefore, "no session D is published");
        otherParticipants.Should().Equal(otherCall ? ["Z"] : []);
        otherEventsAfter.Should().Be(otherEvents);
        held.Should().BeTrue();
        endedAtCompletion.Should().Be(0);
        endedAtX.Should().Be(0);
        endedAtY.Should().Be(1, "the actual Y hangup must end C, not a replacement call");
        causeAtY.Should().Be(HangupCause.UserBusy);
        reloadAtY.Should().BeNull();
        if (other is not null) rig.EndingsOf(other).Should().Be(1);
        unverifiable.Should().Be(0);
        peer.Asked("Status").Should().Be(requests, "the aged ended C supplies no Status candidate");
        pass.Values["ServerId"].Should().Be("a");
        debug.Should().HaveCount(2);
        debug.Select(e => (string)e.Values["ChannelId"]!).Should().BeEquivalentTo(_expectedChannelIds);
        foreach (var entry in debug)
        {
            entry.Values["ServerId"].Should().Be("a");
            entry.Values["DeliveryGeneration"].Should().Be(2L);
            entry.Values["OwnerGeneration"].Should().Be(2L);
        }
        peer.Fault.Should().BeNull();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task OnChannelAdded_ShouldNotOverwriteARegistrationThatWinsTheChannel_WhenItClaimsAfterTheArrivalsLookup()
    {
        var results = new List<RaceResult>();
        foreach (var step in new[] { HandlerStep.JoinFound, HandlerStep.ArrivalHeldById })
        {
            var store = new SaveObserver();
            var peer = new BootingAsterisk { BootedAtLogin = true };
            await using var run = await Run.StartAsync(peer);
            await using var rig = new PoolSweepRig(store: store);
            rig.Manager.AttachToServer(run.Server, "a");
            var events = new ConcurrentQueue<SessionDomainEvent>();
            using var subscription = rig.Manager.Events.Subscribe(events.Enqueue);
            CallSession? target = null;
            if (step == HandlerStep.JoinFound)
            {
                run.Server.Channels.OnNewChannel("Z", "PJSIP/Z", ChannelState.Up, linkedId: "L");
                target = rig.Manager.GetByChannelId("a", "Z")!;
            }
            using var parked = StepParker.Install(rig.Manager, step, "a", "X");
            var arrival = Task.Run(() => run.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L"));
            var winner = new CallSession("winner", "R", "a", CallDirection.Inbound);
            winner.AddParticipant(new SessionParticipant { UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP", Role = ParticipantRole.Caller });
            var candidateId = "";
            var beforeEvents = 0;
            var beforeSaves = 0;
            var targetEvents = 0;
            var targetSaves = 0;
            var starts = 0;
            try
            {
                parked.WaitUntilParked();
                run.Server.Channels.GetByUniqueId("X").Should().NotBeNull();
                rig.Manager.GetByChannelId("a", "X").Should().BeNull();
                if (step == HandlerStep.ArrivalHeldById)
                    candidateId = rig.Manager.ActiveSessions.Single(c => c.LinkedId == "L").SessionId;
                rig.Manager.RegisterReconstructedSession(winner).Should().BeTrue();
                rig.Manager.GetByChannelId("a", "X").Should().BeSameAs(winner, "registration wins the actual X claim before arrival resumes");
                beforeEvents = winner.Events.Count;
                beforeSaves = store.SavesOf(winner.SessionId);
                beforeSaves.Should().Be(1, "accepted registration legitimately persists itself");
                targetEvents = target?.Events.Count ?? 0;
                targetSaves = target is null ? 0 : store.SavesOf(target.SessionId);
                starts = events.OfType<CallStartedEvent>().Count();
            }
            finally
            {
                parked.LetGo();
                await arrival.WaitAsync(SweepRig.Bound);
            }
            parked.ThrowIfFaulted();
            var owner = ReferenceEquals(rig.Manager.GetByChannelId("a", "X"), winner);
            var noWinnerChange = winner.Events.Count == beforeEvents && store.SavesOf(winner.SessionId) == beforeSaves;
            var noTargetChange = target is null || (target.Participants.Select(p => p.UniqueId).SequenceEqual(["Z"])
                && target.Events.Count == targetEvents && store.SavesOf(target.SessionId) == targetSaves);
            var noCandidate = step == HandlerStep.JoinFound || (rig.Manager.GetByLinkedId("a", "L") is null
                && (candidateId.Length == 0 || (rig.Manager.GetById(candidateId) is null
                    && !events.Any(e => e.SessionId == candidateId) && store.SavesOf(candidateId) == 0)));
            var noExtraStart = events.OfType<CallStartedEvent>().Count() == starts;
            var snapshot = rig.Manager.InspectIndexes();
            var expectedSessions = target is null ? 1 : 2;
            var noExtraIndex = snapshot.Sessions.Count == expectedSessions && snapshot.LinkedIds.Count == expectedSessions
                && snapshot.ChannelIds.Count == expectedSessions;
            var invariant = Record.Exception(() => SessionIndexInvariant.Assert(snapshot));
            peer.StatusChannels = target is null
                ? [new StatusChannel("X", "PJSIP/X", "L")]
                : [new StatusChannel("X", "PJSIP/X", "L"), new StatusChannel("Z", "PJSIP/Z", "L")];
            SweepRig.CreatedAtField.SetValue(winner, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
            (rig.Clock.GetUtcNow() - winner.CreatedAt > rig.Options.DialingTimeout).Should().BeTrue();
            rig.Pool!.AddExistingServer("a", run.Server);
            rig.Pool.GetServer("a").Should().BeSameAs(run.Server);
            await rig.StartAsync();
            (await rig.TickAsync()).Should().BeTrue();
            var unverifiable = ScopedSweepDecisionTests.Count(rig, "Unverifiable");
            results.Add(new RaceResult(step, owner, noWinnerChange, noTargetChange, noCandidate, noExtraStart, noExtraIndex, invariant, unverifiable));
            output.WriteLine($"race {step}: ownerC={owner}; noWinnerChange={noWinnerChange}; noTargetChange={noTargetChange}; noCandidate={noCandidate}; noExtraStart={noExtraStart}; noExtraIndex={noExtraIndex}; unverifiable={unverifiable}; registrationSaves={beforeSaves}; indexError={invariant?.Message ?? "none"}");
            peer.Fault.Should().BeNull();
        }
        using var assertions = new AssertionScope();
        foreach (var result in results)
        {
            result.Owner.Should().BeTrue($"{result.Step} must not overwrite registration's actual channel claim");
            result.NoWinnerChange.Should().BeTrue();
            result.NoTargetChange.Should().BeTrue();
            result.NoCandidate.Should().BeTrue();
            result.NoExtraStart.Should().BeTrue();
            result.NoExtraIndex.Should().BeTrue();
            result.Invariant.Should().BeNull();
            result.Unverifiable.Should().Be(0);
        }
    }

    private sealed record RaceResult(HandlerStep Step, bool Owner, bool NoWinnerChange, bool NoTargetChange,
        bool NoCandidate, bool NoExtraStart, bool NoExtraIndex, Exception? Invariant, int Unverifiable);

    private sealed class SaveObserver : SessionStoreBase
    {
        private readonly ConcurrentQueue<string> _saves = new();
        public int SavesOf(string id) => _saves.Count(saved => saved == id);
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _saves.Enqueue(session.SessionId);
            return ValueTask.CompletedTask;
        }
        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
    }
}
