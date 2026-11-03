using System.Runtime.CompilerServices;
using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class DetachedLegacyClassificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_ShouldPreserveTheInitialLegacyCount_WhenItsDetachedObservedCandidateLosesItsTableChannelDuringStatus(bool singleServer)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a");
        await server.Server.ReconcileChannelsAsync();
        server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        call.Unverified.Should().BeFalse();
        rig.Manager.IsPendingDecision(call).Should().BeFalse();
        rig.Manager.DetachFromServer("a");
        var before = SweepRig.Look(call);
        var asks = server.StatusRequests;
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        server.StatusRequests.Should().Be(asks + 1);
        server.Server.Channels.GetByUniqueId("X").Should().BeNull();
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0, "the old legacy candidate classification belongs to this tick");
        rig.EndingsOf(call).Should().Be(0);
        SweepRig.Look(call).Should().BeEquivalentTo(before);
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        server.StatusRequests.Should().Be(asks + 1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(1, "the next tick sees a genuinely observed historical call whose table now holds no channel");
        call.Participants.Single().LeftAt.Should().BeNull("a detached historical table cannot be a live decision oracle");
        rig.EndingsOf(call).Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sweep_ShouldRetainParticipantOnlyLegacyClassification_WhenItDetachesDuringAPendingOrLinkedIdOnlyStatus(bool singleServer, bool linkedOnly)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a", attach: false);
        if (linkedOnly)
        {
            await server.Server.ReconcileChannelsAsync();
            server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        }
        var call = new CallSession("legless", "L", "a", CallDirection.Inbound)
            { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) };
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Manager.AttachToServer(server.Server, "a");
        call.Participants.Should().BeEmpty();
        rig.Manager.IsPendingDecision(call).Should().BeTrue();
        ScopedSweepDecisionTests.Complete(server.Server).Should().Be(linkedOnly);
        server.StatusReply = token => DetachOnRead(rig, token);
        var asks = server.StatusRequests;
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        server.StatusRequests.Should().Be(asks + 1);
        ScopedSweepDecisionTests.Count(rig, "Candidates").Should().Be(1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(1, "a pending/linkedid-only Status candidate was never an old held-participant legacy candidate");
        rig.EndingsOf(call).Should().Be(0);
        call.Participants.Should().BeEmpty();
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        server.StatusRequests.Should().Be(asks + 1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(1);
        SessionIndexInvariant.Assert(rig.Manager);
    }

    private static async IAsyncEnumerable<ManagerEvent> DetachOnRead(PoolSweepRig rig,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        rig.Manager.DetachFromServer("a");
        yield break;
    }

}
