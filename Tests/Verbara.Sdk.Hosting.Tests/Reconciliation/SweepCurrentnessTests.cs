using System.Runtime.CompilerServices;
using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class SweepCurrentnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldRejectTheOldInstanceAfterItsStatus_WhenTheAttachmentChangesDuringTheRead(bool singleServer)
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a");
        var call = first.LostCall(rig.Manager, "call");
        await using var replacement = new RigServerLease("a", rig.Pulse);
        await replacement.Server.Server.ReconcileChannelsAsync();
        foreach (var id in first.UniqueIdsOf("call")) replacement.Server.ChannelWithoutSession(id);
        first.StatusReply = token => ChangeAttachment(rig, replacement.Server.Server, release: false, token);
        await ScopedSweepDecisionTests.Pass(rig, first.Server, singleServer ? null : "a");
        first.StatusRequests.Should().Be(1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        rig.EndingsOf(call).Should().Be(0);
        rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
        rig.Manager.GetByChannelId("a", call.Participants[0].UniqueId).Should().BeSameAs(call);
        call.Participants.Should().OnlyContain(p => p.LeftAt == null);
        call.Metadata.Should().NotContainKey("cause");
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldCountAndDecideNothingAfterRelease_WhenReleaseHappensDuringTheStatusRead(bool singleServer)
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a");
        var call = first.LostCall(rig.Manager, "call");
        var pending = new CallSession("pending-release", "pending-L", "a", CallDirection.Inbound)
            { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) };
        rig.Manager.RegisterReconstructedSession(pending).Should().BeTrue();
        pending.Participants.Should().BeEmpty();
        rig.Manager.IsPendingDecision(pending).Should().BeTrue();
        var beforePending = SweepRig.Look(pending);
        var before = SweepRig.Look(call);
        first.StatusReply = token => ChangeAttachment(rig, null, release: true, token);
        await ScopedSweepDecisionTests.Pass(rig, first.Server, singleServer ? null : "a");
        first.StatusRequests.Should().Be(1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        rig.EndingsOf(call).Should().Be(0);
        rig.Manager.GetById(call.SessionId).Should().BeNull();
        rig.Manager.GetById(pending.SessionId).Should().BeNull();
        rig.Manager.GetByLinkedId("a", call.LinkedId).Should().BeNull();
        rig.Manager.GetByLinkedId("a", pending.LinkedId).Should().BeNull();
        rig.Manager.InspectIndexes().Sessions.Should().NotContain(e => ReferenceEquals(e.Value, call) || ReferenceEquals(e.Value, pending));
        rig.Manager.InspectIndexes().ChannelIds.Should().NotContain(e => ReferenceEquals(e.Value, call) || ReferenceEquals(e.Value, pending));
        rig.Manager.InspectIndexes().Bridges.Should().NotContain(e => e.Value == call.SessionId || e.Value == pending.SessionId);
        rig.EndingsOf(pending).Should().Be(0);
        SweepRig.Look(call).Should().BeEquivalentTo(before);
        SweepRig.Look(pending).Should().BeEquivalentTo(beforePending);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Sweep_ShouldUseOnlyExplicitPoolIdentityForLegacyCounts_WhenTheCallHasNeverHadAnAttachment(bool singleServer, int expected)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a", attach: false);
        await server.Server.ReconcileChannelsAsync();
        var call = new CallSession("never-attached", "L", "a", CallDirection.Inbound)
            { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) };
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var before = SweepRig.Look(call);
        var asks = server.StatusRequests;
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(expected);
        server.StatusRequests.Should().Be(asks);
        SweepRig.Look(call).Should().BeEquivalentTo(before);
        rig.EndingsOf(call).Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldUseDetachedHistoryOnlyForLegacyCounting_WhenTheLatestInstanceNoLongerHasASubscription(bool singleServer)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a");
        await server.Server.ReconcileChannelsAsync();
        server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        call.Unverified.Should().BeFalse();
        rig.Manager.DetachFromServer("a");
        server.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        var before = SweepRig.Look(call);
        var asks = server.StatusRequests;
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(1);
        server.StatusRequests.Should().Be(asks);
        rig.EndingsOf(call).Should().Be(0, "historical identity cannot arm a live-table decision against a detached instance");
        SweepRig.Look(call).Should().BeEquivalentTo(before);
        await using var replacement = new RigServerLease("a", rig.Pulse);
        await replacement.Server.Server.ReconcileChannelsAsync();
        replacement.Server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        rig.Manager.AttachToServer(replacement.Server.Server, "a");
        rig.Manager.DetachFromServer("a");
        replacement.Server.Server.Channels.OnHangup("X", HangupCause.NormalClearing);
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0, "the older detached instance no longer owns even legacy counting");
        await ScopedSweepDecisionTests.Pass(rig, replacement.Server.Server, singleServer ? null : "a");
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(1);
        rig.EndingsOf(call).Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    private static async IAsyncEnumerable<ManagerEvent> ChangeAttachment(PoolSweepRig rig, VerbaraServer? replacement,
        bool release, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        rig.Manager.DetachFromServer("a", release ? DetachedCalls.Release : DetachedCalls.Keep);
        if (replacement is not null) rig.Manager.AttachToServer(replacement, "a");
        yield break;
    }
}

internal sealed class RigServerLease(string id, Pulse pulse) : IAsyncDisposable
{
    public RigServer Server { get; } = new(id, pulse);
    public ValueTask DisposeAsync() => Server.Server.DisposeAsync();
}
