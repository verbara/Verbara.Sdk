using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using FluentAssertions;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class ServerScopedSessionBaselineTests : IAsyncLifetime
{
    private readonly TwoServerSessionRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public void OnChannelAdded_ShouldOpenTwoCalls_WhenTwoServersReportTheSameChannelAndLinkedId()
    {
        var starts = new List<CallStartedEvent>();
        using var subscription = _rig.Manager.Events.OfType<CallStartedEvent>().Subscribe(starts.Add);

        TwoServerSessionRig.AddChannels(_rig.A, "1700000000.1", "1700000000.1");
        TwoServerSessionRig.AddChannels(_rig.B, "1700000000.1", "1700000000.1");

        var calls = _rig.Manager.InspectIndexes().Sessions.Select(e => e.Value).ToArray();
        calls.Should().HaveCount(2, "each reporting server owns its own call even when both identifiers collide");
        calls.Select(c => c.ServerId).Should().BeEquivalentTo("a", "b");
        calls.Should().OnlyContain(c => c.Participants.Count == 1);
        starts.Should().HaveCount(2);
        _rig.FindCurrentByLinkedId("a", "1700000000.1").Should().NotBeNull();
        _rig.FindCurrentByLinkedId("b", "1700000000.1").Should().NotBeNull();
    }

    [Fact]
    public void OnHangup_ShouldLeaveTheOtherServersCallInProgress_WhenBothServersReportTheSameChannelId()
    {
        var a = _rig.Open(_rig.A, "shared", "x");
        TwoServerSessionRig.AddChannels(_rig.B, "shared", "x");

        _rig.B.Server.Channels.OnHangup("x");

        a.EndingDelivered.Should().BeFalse("b's hangup cannot end a's call");
        a.Participants.Single().LeftAt.Should().BeNull();
        _rig.Pool.EndingsOf(a).Should().Be(0);
        _rig.A.Server.Channels.GetByUniqueId("x").Should().NotBeNull();
        var b = _rig.FindCurrentByLinkedId("b", "shared");
        b.Should().NotBeNull();
        b!.EndingDelivered.Should().BeTrue();
        _rig.Pool.EndingsOf(b).Should().Be(1);
    }

    [Fact]
    public async Task Reattach_ShouldEndTheLeftCallOnce_WhenAnExplicitReconciliationCompletesItsEmptyTable()
    {
        var left = _rig.Open(_rig.A, "left", "x");
        _rig.Open(_rig.B, "other", "y");
        _rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        _rig.A.Server.Channels.OnHangup("x");
        left.EndingDelivered.Should().BeFalse("the manager missed the hangup while detached");
        _rig.A.Server.Channels.GetByUniqueId("x").Should().BeNull();
        _rig.Manager.AttachToServer(_rig.A.Server, "a");

        await _rig.Track(_rig.A.Server.ReconcileChannelsAsync().AsTask());
        await _rig.Track(_rig.A.Server.ReconcileChannelsAsync().AsTask());

        _rig.A.StatusRequests.Should().Be(2, "both explicit empty snapshots completed; no sweep timer was started");
        left.EndingDelivered.Should().BeTrue("a complete returning table proves that the left call has no channel");
        _rig.Pool.EndingsOf(left).Should().Be(1);
        _rig.Manager.InspectIndexes().ChannelIds.Should().NotContain(e => ReferenceEquals(e.Value, left));
    }

    [Fact]
    public void RegisterReconstructedSession_ShouldAcceptTheOtherServersCall_WhenItsLinkedIdIsHeldOnlyOnA()
    {
        var a = _rig.Open(_rig.A, "shared", "a-leg");
        var reconstructed = Reconstructed("b-restored", "shared", "b", "b-leg");

        var accepted = _rig.Manager.RegisterReconstructedSession(reconstructed);

        accepted.Should().BeTrue("a's linkedid cannot refuse b's reconstructed call");
        _rig.Manager.GetById(a.SessionId).Should().BeSameAs(a);
        _rig.Manager.GetById(reconstructed.SessionId).Should().BeSameAs(reconstructed);
        _rig.FindCurrentByLinkedId("a", "shared").Should().BeSameAs(a);
        _rig.FindCurrentByLinkedId("b", "shared").Should().BeSameAs(reconstructed);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public void RegisterReconstructedSession_ShouldRefuseASecondInstance_WhenItsSessionIdIsHeldAndItsLinkedIdIsFree(string serverId)
    {
        var original = _rig.Open(_rig.A, "held-linked", "held-leg");
        var before = _rig.Manager.InspectIndexes();
        var duplicate = Reconstructed(original.SessionId, "free-linked", serverId, "free-leg");

        var accepted = _rig.Manager.RegisterReconstructedSession(duplicate);

        accepted.Should().BeFalse("the global held session id already belongs to another instance");
        _rig.Manager.GetById(original.SessionId).Should().BeSameAs(original);
        _rig.Manager.InspectIndexes().Should().BeEquivalentTo(before);
    }

    [Fact]
    public void RegisterReconstructedSession_ShouldRefuseAndPreserveEveryIndex_WhenItsOwnServerAlreadyMapsOneOfItsLegs()
    {
        var original = _rig.Open(_rig.A, "original-linked", "x");
        var before = _rig.Manager.InspectIndexes();
        var conflicting = Reconstructed("conflicting", "new-linked", "a", "x");

        var accepted = _rig.Manager.RegisterReconstructedSession(conflicting);

        accepted.Should().BeFalse("a already maps x to another held call");
        _rig.Manager.GetById(original.SessionId).Should().BeSameAs(original);
        _rig.Manager.GetById(conflicting.SessionId).Should().BeNull();
        _rig.Manager.InspectIndexes().Should().BeEquivalentTo(before);
    }

    private static CallSession Reconstructed(string id, string linkedId, string serverId, params string[] legs)
    {
        var call = new CallSession(id, linkedId, serverId, CallDirection.Inbound);
        foreach (var leg in legs)
            call.AddParticipant(new SessionParticipant { UniqueId = leg, Channel = $"PJSIP/100-{leg}", Technology = "PJSIP" });
        return call;
    }
}
