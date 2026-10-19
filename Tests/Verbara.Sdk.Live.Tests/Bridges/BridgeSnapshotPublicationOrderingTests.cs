using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Bridges;
using Verbara.Sdk.Live.Tests.Queues;

namespace Verbara.Sdk.Live.Tests.Bridges;

/// <summary>Exercises the manager's real transitions at their internal publication boundary.</summary>
[Collection(LiveCountersGroup.Name)]
public sealed class BridgeSnapshotPublicationOrderingTests
{
    private const string Channel = "channel.1";
    private const string OldBridge = "old";
    private const string NewBridge = "new";

    public enum LiveChange
    {
        Leave,
        Destroy,
        Move,
    }

    [Theory]
    [InlineData(true, LiveChange.Leave)]
    [InlineData(false, LiveChange.Leave)]
    [InlineData(true, LiveChange.Destroy)]
    [InlineData(false, LiveChange.Destroy)]
    [InlineData(true, LiveChange.Move)]
    [InlineData(false, LiveChange.Move)]
    public void ReconcileWithSnapshot_ShouldNotPublishAnObsoleteOwner_WhenLiveChangesAfterMembershipIsApplied(
        bool removeOmitted, LiveChange change)
    {
        var sut = Manager();
        Create(sut, NewBridge);
        using var window = sut.OpenSnapshotWindow();
        var observerCalls = 0;
        var entered = new List<string>();
        sut.ChannelEntered += (bridge, _) => entered.Add(bridge.BridgeUniqueid);
        sut.TransitionObserver = (point, bridge) =>
        {
            if (point != BridgeTransitionPoint.SnapshotMembershipApplied || bridge.BridgeUniqueid != OldBridge)
                return;
            sut.TransitionObserver = null;
            observerCalls++;
            Apply(sut, change);
        };

        sut.ReconcileWithSnapshot(window, [Listed(OldBridge), Listed(NewBridge)],
            Members((OldBridge, [Channel])), removeOmitted);

        using var assertions = new AssertionScope();
        observerCalls.Should().Be(1, "the live operation ran at the snapshot membership publication boundary");
        AssertAfterLiveChange(sut, change);
        entered.Should().BeEquivalentTo(change == LiveChange.Move ? new[] { NewBridge } : [],
            "snapshot membership is silent; only the actual live move announces an entry");
    }

    [Theory]
    [InlineData(LiveChange.Leave)]
    [InlineData(LiveChange.Destroy)]
    [InlineData(LiveChange.Move)]
    public void OnChannelEntered_ShouldNotPublishAnObsoleteOwner_WhenLiveChangesAfterMembershipIsApplied(LiveChange change)
    {
        var sut = Manager();
        Create(sut, OldBridge);
        Create(sut, NewBridge);
        var observerCalls = 0;
        sut.TransitionObserver = (point, bridge) =>
        {
            if (point != BridgeTransitionPoint.LiveMembershipApplied || bridge.BridgeUniqueid != OldBridge)
                return;
            sut.TransitionObserver = null;
            observerCalls++;
            Apply(sut, change);
        };

        sut.OnChannelEntered(OldBridge, Channel);

        using var assertions = new AssertionScope();
        observerCalls.Should().Be(1);
        AssertAfterLiveChange(sut, change);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ReconcileWithSnapshot_ShouldKeepTheNewLiveOwner_WhenItsEntryFollowsTheRequestBeforeAnOldRowIsApplied(
        bool removeOmitted, bool oldOwnerWasHeld)
    {
        var sut = Manager();
        if (oldOwnerWasHeld)
        {
            Create(sut, OldBridge);
            sut.OnChannelEntered(OldBridge, Channel);
        }
        Create(sut, NewBridge);
        using var window = sut.OpenSnapshotWindow();
        // Asterisk's BridgeEnter in the new bridge can precede delivery of BridgeLeave for the old one.
        sut.OnChannelEntered(NewBridge, Channel);

        sut.ReconcileWithSnapshot(window, [Listed(OldBridge), Listed(NewBridge)],
            Members((OldBridge, [Channel])), removeOmitted);

        using var assertions = new AssertionScope();
        sut.GetById(OldBridge)!.Channels.Should().NotContainKey(Channel,
            "the old row cannot retain membership that belongs to a newer live owner");
        sut.GetById(NewBridge)!.Channels.Should().ContainKey(Channel);
        sut.GetBridgeForChannel(Channel).Should().BeSameAs(sut.GetById(NewBridge));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void ReconcileWithSnapshot_ShouldRepairTheHeldMembersLookup_WhenACompletedRowSupersedesAnOlderLiveOwner(
        bool reverseRows, bool removeOmitted)
    {
        var sut = Manager();
        Create(sut, OldBridge);
        Create(sut, NewBridge);
        sut.OnChannelEntered(OldBridge, Channel);
        sut.OnChannelEntered(NewBridge, Channel);
        using var window = sut.OpenSnapshotWindow();
        BridgeSnapshotEntry[] listed = reverseRows
            ? [Listed(NewBridge), Listed(OldBridge)]
            : [Listed(OldBridge), Listed(NewBridge)];

        sut.ReconcileWithSnapshot(window, listed, Members((OldBridge, [Channel])), removeOmitted);

        using var assertions = new AssertionScope();
        sut.GetById(OldBridge)!.Channels.Should().ContainKey(Channel);
        if (removeOmitted)
            sut.GetById(NewBridge)!.Channels.Should().NotContainKey(Channel);
        else
            sut.GetById(NewBridge)!.Channels.Should().ContainKey(Channel, "timeout admits but does not remove omitted membership");
        sut.GetBridgeForChannel(Channel).Should().BeSameAs(sut.GetById(OldBridge),
            "a valid held membership must publish the completed snapshot's owner, not just newly inserted members");
    }

    [Theory]
    [InlineData(LiveChange.Leave, true)]
    [InlineData(LiveChange.Destroy, true)]
    [InlineData(LiveChange.Leave, false)]
    [InlineData(LiveChange.Destroy, false)]
    public void OldOwnerCleanup_ShouldKeepTheNewOwnersLookup_WhenTheChannelHasMoved(
        LiveChange cleanup, bool oldOwnerWasHeld)
    {
        var sut = Manager();
        if (oldOwnerWasHeld)
        {
            Create(sut, OldBridge);
            sut.OnChannelEntered(OldBridge, Channel);
        }
        Create(sut, NewBridge);
        sut.OnChannelEntered(NewBridge, Channel);

        Apply(sut, cleanup);

        using var assertions = new AssertionScope();
        sut.GetById(NewBridge)!.Channels.Should().ContainKey(Channel);
        sut.GetBridgeForChannel(Channel).Should().BeSameAs(sut.GetById(NewBridge),
            "cleanup of an old or unknown bridge can remove only a mapping still owned by that bridge");
        if (cleanup == LiveChange.Leave && oldOwnerWasHeld)
            sut.GetById(OldBridge)!.Channels.Should().NotContainKey(Channel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconcileWithSnapshot_ShouldCountAdmissionBeforeItsDestructionIsVisible_WhenTheBridgeIsDestroyedAtAdmission(
        bool removeOmitted)
    {
        var sut = Manager();
        using var counters = new LiveCounters();
        using var window = sut.OpenSnapshotWindow();
        int? activeAtDestruction = null;
        int? insertedAtDestruction = null;
        long? createdAtDestruction = null;
        long? destroyedAtDestruction = null;
        sut.BridgeDestroyed += _ =>
        {
            activeAtDestruction = sut.ActiveBridgeCount;
            insertedAtDestruction = sut.BridgeCount;
            createdAtDestruction = counters.BridgesCreated;
            destroyedAtDestruction = counters.BridgesDestroyed;
        };
        var observerCalls = 0;
        sut.TransitionObserver = (point, bridge) =>
        {
            if (point != BridgeTransitionPoint.SnapshotAdmitted)
                return;
            sut.TransitionObserver = null;
            observerCalls++;
            sut.OnBridgeDestroyed(bridge.BridgeUniqueid);
        };

        sut.ReconcileWithSnapshot(window, [Listed(OldBridge)], Members((OldBridge, [Channel])), removeOmitted);

        using var assertions = new AssertionScope();
        observerCalls.Should().Be(1);
        activeAtDestruction.Should().Be(0, "an admitted bridge is counted before a destroy handler can observe it");
        insertedAtDestruction.Should().Be(1);
        createdAtDestruction.Should().Be(1);
        destroyedAtDestruction.Should().Be(1);
        sut.ActiveBridgeCount.Should().Be(0);
        sut.BridgeCount.Should().Be(1);
        counters.BridgesCreated.Should().Be(1);
        counters.BridgesDestroyed.Should().Be(1);
        sut.GetById(OldBridge)!.DestroyedAt.Should().NotBeNull();
        sut.GetBridgeForChannel(Channel).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconcileWithSnapshot_ShouldCountOneAdmission_WhenNoLiveChangeInterleaves(bool removeOmitted)
    {
        var sut = Manager();
        using var counters = new LiveCounters();
        using var window = sut.OpenSnapshotWindow();
        var createdEvents = 0;
        var enteredEvents = 0;
        sut.BridgeCreated += _ => createdEvents++;
        sut.ChannelEntered += (_, _) => enteredEvents++;

        sut.ReconcileWithSnapshot(window, [Listed(OldBridge)], Members((OldBridge, [Channel])), removeOmitted);

        using var assertions = new AssertionScope();
        sut.BridgeCount.Should().Be(1);
        sut.ActiveBridgeCount.Should().Be(1);
        counters.BridgesCreated.Should().Be(1);
        counters.BridgesDestroyed.Should().Be(0);
        createdEvents.Should().Be(0);
        enteredEvents.Should().Be(0);
        sut.GetById(OldBridge)!.Channels.Should().ContainKey(Channel);
        sut.GetBridgeForChannel(Channel).Should().BeSameAs(sut.GetById(OldBridge));
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldNotPublishTheDetachedLifetime_WhenAdmissionReentersWithClearAndRecreation()
    {
        var sut = Manager();
        using var window = sut.OpenSnapshotWindow();
        AsteriskBridge? detached = null;
        sut.TransitionObserver = (point, bridge) =>
        {
            if (point != BridgeTransitionPoint.SnapshotAdmitted)
                return;
            sut.TransitionObserver = null;
            detached = bridge;
            sut.Clear();
            Create(sut, OldBridge);
            sut.OnChannelEntered(OldBridge, "replacement.1");
        };

        sut.ReconcileWithSnapshot(window, [Listed(OldBridge)], Members((OldBridge, [Channel])), removeOmitted: true);

        using var assertions = new AssertionScope();
        sut.GetById(OldBridge).Should().NotBeSameAs(detached);
        detached!.Channels.Should().BeEmpty("the old admission lost its lifetime during the callback");
        sut.GetById(OldBridge)!.Channels.Keys.Should().BeEquivalentTo(["replacement.1"]);
        sut.GetBridgeForChannel(Channel).Should().BeNull();
        sut.GetBridgeForChannel("replacement.1").Should().BeSameAs(sut.GetById(OldBridge));
        sut.BridgeCount.Should().Be(1, "Clear starts the same public reset boundary as before");
        sut.ActiveBridgeCount.Should().Be(1);
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldNotDestroyARecreatedLifetime_WhenAnEarlierDestructionCallbackClearsTheManager()
    {
        var sut = Manager();
        Create(sut, OldBridge);
        Create(sut, NewBridge);
        using var window = sut.OpenSnapshotWindow();
        var destroyed = new List<AsteriskBridge>();
        var replaced = false;
        AsteriskBridge? replacement = null;
        sut.BridgeDestroyed += bridge =>
        {
            destroyed.Add(bridge);
            if (replaced)
                return;
            replaced = true;
            sut.Clear();
            Create(sut, OldBridge);
            Create(sut, NewBridge);
            replacement = sut.GetById(NewBridge);
            sut.OnChannelEntered(NewBridge, Channel);
        };

        sut.ReconcileWithSnapshot(window, [], Members(), removeOmitted: true);

        using var assertions = new AssertionScope();
        destroyed.Should().HaveCount(1, "the enumeration's remaining objects belong to the pre-Clear lifetimes");
        sut.GetById(NewBridge).Should().BeSameAs(replacement);
        replacement!.DestroyedAt.Should().BeNull();
        replacement.Channels.Should().ContainKey(Channel);
        sut.GetBridgeForChannel(Channel).Should().BeSameAs(replacement);
        sut.BridgeCount.Should().Be(2);
        sut.ActiveBridgeCount.Should().Be(2);
    }

    public enum ObservedTransition
    {
        SnapshotAdmission,
        SnapshotMembership,
        LiveMembership,
    }

    [Theory]
    [InlineData(ObservedTransition.SnapshotAdmission)]
    [InlineData(ObservedTransition.SnapshotMembership)]
    [InlineData(ObservedTransition.LiveMembership)]
    public void TransitionObserver_ShouldInvokeTheConfiguredHook_WhenItsTransitionIsApplied(ObservedTransition transition)
    {
        var sut = Manager();
        var expected = transition switch
        {
            ObservedTransition.SnapshotAdmission => BridgeTransitionPoint.SnapshotAdmitted,
            ObservedTransition.SnapshotMembership => BridgeTransitionPoint.SnapshotMembershipApplied,
            _ => BridgeTransitionPoint.LiveMembershipApplied,
        };
        var calls = 0;
        sut.TransitionObserver = (point, _) =>
        {
            if (point == expected)
                calls++;
        };

        RunPoint(sut, expected);

        calls.Should().Be(1);
    }

    [Fact]
    public void TransitionObserver_ShouldBeInertByDefault_WhenTheManagerAppliesSnapshotMembership()
    {
        var sut = Manager();
        sut.TransitionObserver.Should().BeNull();
        RunPoint(sut, BridgeTransitionPoint.SnapshotMembershipApplied);
        sut.GetById(OldBridge)!.Channels.Should().ContainKey(Channel);
        sut.GetBridgeForChannel(Channel).Should().BeSameAs(sut.GetById(OldBridge));
    }

    [Fact]
    public void TransitionObserver_ShouldBeInertByDefault_WhenTheManagerAppliesLiveMembership()
    {
        var sut = Manager();
        sut.TransitionObserver.Should().BeNull();
        RunPoint(sut, BridgeTransitionPoint.LiveMembershipApplied);
        sut.GetById(OldBridge)!.Channels.Should().ContainKey(Channel);
        sut.GetBridgeForChannel(Channel).Should().BeSameAs(sut.GetById(OldBridge));
    }

    private static void RunPoint(BridgeManager sut, BridgeTransitionPoint point)
    {
        if (point == BridgeTransitionPoint.LiveMembershipApplied)
        {
            Create(sut, OldBridge);
            sut.OnChannelEntered(OldBridge, Channel);
            return;
        }
        using var window = sut.OpenSnapshotWindow();
        sut.ReconcileWithSnapshot(window, [Listed(OldBridge)], Members((OldBridge, [Channel])), removeOmitted: true);
    }

    private static void AssertAfterLiveChange(BridgeManager sut, LiveChange change)
    {
        var old = sut.GetById(OldBridge)!;
        if (change == LiveChange.Destroy)
        {
            old.DestroyedAt.Should().NotBeNull();
            // A destroyed bridge keeps its channel IDs while the bridge object remains inside the retention window.
            old.Channels.Should().ContainKey(Channel);
        }
        else
            old.Channels.Should().NotContainKey(Channel);
        if (change == LiveChange.Move)
        {
            sut.GetById(NewBridge)!.Channels.Should().ContainKey(Channel);
            sut.GetBridgeForChannel(Channel).Should().BeSameAs(sut.GetById(NewBridge),
                "the completed live move owns the channel even when an older publication resumes");
        }
        else
            sut.GetBridgeForChannel(Channel).Should().BeNull(
                "a completed leave or destruction cannot be followed by stale publication of its former owner");
    }

    private static void Apply(BridgeManager sut, LiveChange change)
    {
        if (change == LiveChange.Destroy)
            sut.OnBridgeDestroyed(OldBridge);
        else
        {
            sut.OnChannelLeft(OldBridge, Channel);
            if (change == LiveChange.Move)
                sut.OnChannelEntered(NewBridge, Channel);
        }
    }

    private static BridgeManager Manager() => new(NullLogger.Instance);
    private static void Create(BridgeManager sut, string id) => sut.OnBridgeCreated(id, "basic", "simple_bridge", null, null);
    private static BridgeSnapshotEntry Listed(string id) => new(id, "basic", "simple_bridge", null, null);
    private static Dictionary<string, IReadOnlyCollection<string>> Members(params (string Bridge, string[] Channels)[] members) =>
        members.ToDictionary(static entry => entry.Bridge, static entry => (IReadOnlyCollection<string>)entry.Channels);
}
