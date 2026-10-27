using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class TwoServerSessionRigTests : IAsyncLifetime
{
    private readonly TwoServerSessionRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    [Fact]
    public void Rig_ShouldObserveEveryActualIndex_WhenAReconstructedCallCarriesABridgeAndTwoLegs()
    {
        var call = Reconstructed("reconstructed", "a", "x", "y");
        call.BridgeId = "bridge";
        _rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var other = _rig.Open(_rig.B, "other", "z");

        var entries = _rig.Manager.InspectIndexes();

        entries.Sessions.Should().HaveCount(2);
        entries.LinkedIds.Should().HaveCount(2);
        entries.ChannelIds.Should().HaveCount(3);
        entries.Bridges.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("bridge", call.SessionId));
        entries.ChannelIds.Single(e => e.Key == "z").Value.Should().BeSameAs(other);
        _rig.FindCurrentByLinkedId("b", other.LinkedId).Should().BeSameAs(other);
        _rig.FindCurrentByLinkedId("a", other.LinkedId).Should().BeNull("the test resolver must not return another server's owner");
    }

    [Fact]
    public async Task Rig_ShouldAllowAHistoricalRetiredRow_WhenACompleteSnapshotKeepsOnlyTheOtherLeg()
    {
        var call = _rig.Open(_rig.A, "mixed", "x", "y");
        _rig.Open(_rig.B, "other", "z");
        _rig.A.StatusReply = _ => OnlyY();

        await _rig.Track(_rig.A.Server.ReconcileChannelsAsync().AsTask());

        call.Participants.Single(p => p.UniqueId == "x").LeftAt.Should().NotBeNull();
        call.Participants.Single(p => p.UniqueId == "x").HangupCause.Should().BeNull();
        call.Participants.Single(p => p.UniqueId == "y").LeftAt.Should().BeNull();
        _rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
        call.EndingDelivered.Should().BeFalse();
        _rig.Manager.InspectIndexes().ChannelIds.Select(e => e.Key).Should().BeEquivalentTo("y", "z");
    }

    [Fact]
    public void Rig_ShouldAllowNoLegEntry_WhenAnEndedCallRemainsHeldInRetention()
    {
        var call = _rig.Open(_rig.A, "ended", "x", "y");
        _rig.Open(_rig.B, "other", "z");

        _rig.A.Server.Channels.OnHangup("x");
        _rig.A.Server.Channels.OnHangup("y");

        call.EndingDelivered.Should().BeTrue();
        _rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
        _rig.Manager.GetRecentCompleted().Should().Contain(c => ReferenceEquals(c, call));
        _rig.Manager.InspectIndexes().ChannelIds.Should().NotContain(e => ReferenceEquals(e.Value, call));
        _rig.Manager.InspectIndexes().LinkedIds.Should().Contain(e => ReferenceEquals(e.Value, call));
    }

    [Fact]
    public void Rig_ShouldObserveNoEntryForAReleasedInstance_WhenItsServerReleasesTheCall()
    {
        var call = Reconstructed("released", "a", "x", "y");
        call.BridgeId = "bridge";
        _rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        _rig.Open(_rig.B, "other", "z");

        _rig.Manager.DetachFromServer("a", DetachedCalls.Release).Should().ContainSingle().Which.Should().BeSameAs(call);

        var entries = _rig.Manager.InspectIndexes();
        entries.Sessions.Concat(entries.LinkedIds).Concat(entries.ChannelIds).Should().NotContain(e => ReferenceEquals(e.Value, call));
        entries.Bridges.Should().NotContain(e => e.Value == call.SessionId);
        call.EndingDelivered.Should().BeFalse();
    }

    [Fact]
    public async Task Rig_ShouldAwaitTheOwnedNotificationFence_WhenAConcurrentProducerIsExplicitlyGated()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _rig.Manager.Events.OfType<CallStartedEvent>().Subscribe(_ => started.TrySetResult());
        _rig.AfterNotification(started.Task);
        var driver = _rig.Track(Task.Run(async () =>
        {
            await permit.Task;
            _rig.Open(_rig.A, "gated", "x");
        }));
        var check = _rig.AssertIndexesAsync();
        check.IsCompleted.Should().BeFalse("the scenario has not released its causal gate");

        permit.SetResult();
        await driver.WaitAsync(SweepRig.Bound);
        await check.WaitAsync(SweepRig.Bound);

        started.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Theory]
    [InlineData("session-key")]
    [InlineData("linked-key")]
    [InlineData("channel-key")]
    [InlineData("bridge-key")]
    [InlineData("released-channel")]
    [InlineData("released-linked")]
    [InlineData("orphan-bridge")]
    [InlineData("replacement-instance")]
    [InlineData("missing-present-leg")]
    [InlineData("wrong-server")]
    public void Invariant_ShouldRejectCorruptEntries_WhenASnapshotContainsAnInvalidMapping(string fault)
    {
        var call = Reconstructed("original", "a", "x");
        call.BridgeId = "bridge";
        _rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var entries = _rig.Manager.InspectIndexes();
        var unheld = Reconstructed("unheld", "a", "unknown-channel");
        var otherServer = Reconstructed("other-server", "b", "x");
        var broken = fault switch
        {
            "session-key" => entries with { Sessions = [new("wrong", call)] },
            "linked-key" => entries with { LinkedIds = [new("wrong", call)] },
            "channel-key" => entries with { ChannelIds = [new("wrong", call)] },
            "bridge-key" => entries with { Bridges = [new("wrong", call.SessionId)] },
            "released-channel" => entries with { ChannelIds = [.. entries.ChannelIds, new("unknown-channel", unheld)] },
            "released-linked" => entries with { LinkedIds = [.. entries.LinkedIds, new(unheld.LinkedId, unheld)] },
            "orphan-bridge" => entries with { Bridges = [.. entries.Bridges, new("unknown-bridge", "unheld")] },
            "replacement-instance" => entries with { LinkedIds = [new(call.LinkedId, Reconstructed(call.SessionId, "a", "x"))] },
            "missing-present-leg" => entries with { ChannelIds = [] },
            "wrong-server" => entries with
            {
                Sessions = [.. entries.Sessions, new(otherServer.SessionId, otherServer)],
                ChannelIds = [new("x", otherServer)],
            },
            _ => throw new InvalidOperationException(fault),
        };

        var check = () => SessionIndexInvariant.Assert(broken);

        check.Should().Throw<InvalidOperationException>().WithMessage("Session index invariant:*");
        SessionIndexInvariant.Assert(_rig.Manager);
    }

    private static CallSession Reconstructed(string id, string server, params string[] legs)
    {
        var call = new CallSession(id, $"{id}-linked", server, CallDirection.Inbound);
        foreach (var leg in legs)
            call.AddParticipant(new SessionParticipant { UniqueId = leg, Channel = $"PJSIP/100-{leg}", Technology = "PJSIP" });
        return call;
    }

    private static async IAsyncEnumerable<ManagerEvent> OnlyY()
    {
        await Task.CompletedTask;
        yield return new StatusEvent
        {
            UniqueId = "y", Channel = "PJSIP/100-y", LinkedId = "mixed",
            RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Uniqueid"] = "y", ["Channel"] = "PJSIP/100-y", ["Linkedid"] = "mixed", ["ChannelState"] = "6",
            },
        };
    }
}
