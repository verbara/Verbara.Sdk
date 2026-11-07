using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class LegacySweepScopeTests
{
    [Fact]
    public async Task Sweep_ShouldNotCountTheReleasedCall_WhenItsInitialActiveSnapshotReleasesBeforeReturning()
    {
        var result = await SnapshotWindow(1);
        result.Released.Should().BeTrue("the getter actually released C after copying it and before returning its stale reference");
        result.Unverifiable.Should().Be(0, "the held identity must be rechecked under C's lock after selection");
        result.Status.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_ShouldNotCountTheReleasedCall_WhenItsRefreshedActiveSnapshotReleasesBeforeReturning()
    {
        var result = await SnapshotWindow(2);
        result.Released.Should().BeTrue("the refreshed snapshot itself is stale before the first call lock is acquired");
        result.Unverifiable.Should().Be(0);
        result.Reads.Should().BeLessThanOrEqualTo(2);
    }

    private static async Task<(bool Released, int Unverifiable, int Status, int Reads)> SnapshotWindow(int read)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a", attach: false);
        await using var manager = new LegacySweepManager { ReleaseOnSnapshot = read };
        var call = new CallSession("C", "L", "a", CallDirection.Inbound)
            { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) };
        if (read == 1)
        {
            server.ChannelWithoutSession("X");
            call.AddParticipant(new SessionParticipant { UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP" });
        }
        manager.Held.Add(call.SessionId, call);
        await new ServerSweep(manager, rig.Options, rig.Log.CreateLogger("legacy"), rig.Clock)
            .SweepAsync(server.Server, null, CancellationToken.None);
        return (manager.Released, ScopedSweepDecisionTests.Count(rig, "Unverifiable"), server.StatusRequests, manager.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldRetainLegacyClassificationWithBoundedGetters_WhenVerificationReleasesTheHeldCalls(bool released)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a", attach: false);
        server.ChannelWithoutSession("X");
        await using var manager = new LegacySweepManager();
        for (var i = 0; i < 100; i++)
        {
            var call = new CallSession("C" + i, "L" + i, "a", CallDirection.Inbound)
                { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) };
            manager.Held.Add(call.SessionId, call);
        }
        var candidate = new CallSession("candidate", "K", "a", CallDirection.Inbound)
            { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) };
        candidate.AddParticipant(new SessionParticipant { UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP" });
        manager.Held.Add(candidate.SessionId, candidate);
        server.StatusReply = token => Reply(manager, released, token);
        await new ServerSweep(manager, rig.Options, rig.Log.CreateLogger("legacy"), rig.Clock)
            .SweepAsync(server.Server, null, CancellationToken.None);
        server.StatusRequests.Should().Be(1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(released ? 0 : 100);
        manager.Reads.Should().BeLessThanOrEqualTo(2);
        candidate.EndingDelivered.Should().BeFalse("the legacy manager offers no attachment/table decision oracle");
    }

    private static async IAsyncEnumerable<ManagerEvent> Reply(LegacySweepManager manager, bool released,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (released) manager.Held.Clear();
        yield break;
    }
}

internal sealed class LegacySweepManager : ICallSessionManager
{
    public Dictionary<string, CallSession> Held { get; } = new(StringComparer.Ordinal);
    public int Reads { get; private set; }
    public int ReleaseOnSnapshot { get; init; }
    public bool Released { get; private set; }
    public IEnumerable<CallSession> ActiveSessions
    {
        get
        {
            if (Held.Values.Any(c => c.SyncRoot.IsHeldByCurrentThread)) throw new InvalidOperationException("global getter under a call lock");
            var snapshot = Held.Values.ToArray();
            if (++Reads == ReleaseOnSnapshot) { Held.Clear(); Released = true; }
            return snapshot;
        }
    }
    public CallSession? GetById(string sessionId) => Held.GetValueOrDefault(sessionId);
    public CallSession? GetByLinkedId(string linkedId) => Held.Values.FirstOrDefault(c => c.LinkedId == linkedId);
    public CallSession? GetByChannelId(string uniqueId) => Held.Values.FirstOrDefault(c => c.Participants.Any(p => p.UniqueId == uniqueId));
    public CallSession? GetByBridgeId(string bridgeId) => Held.Values.FirstOrDefault(c => c.BridgeId == bridgeId);
    public IEnumerable<CallSession> GetRecentCompleted(int count = 100) => [];
    public IObservable<SessionDomainEvent> Events => Observable.Empty<SessionDomainEvent>();
    public void AttachToServer(VerbaraServer server, string serverId) { }
    public void DetachFromServer(string serverId) { }
    public bool RegisterReconstructedSession(CallSession session) => Held.TryAdd(session.SessionId, session);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
