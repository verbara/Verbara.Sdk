using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Bridges;
using Verbara.Sdk.Live.Tests.Queues;

namespace Verbara.Sdk.Live.Tests.Bridges;

/// <summary>
/// The bridge table's reconciliation against a load's snapshot holds to the read window: a bridge or a channel entry
/// newer than the snapshot's request is not removed, a destruction or a leave after it is not undone, an answer whose
/// completion cannot be told only admits, and the counters follow the table's transitions.
/// </summary>
/// <remarks>The counters are process-wide, so the class runs in <see cref="LiveCountersGroup"/>.</remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class BridgeSnapshotReconciliationTests
{
    private static readonly string[] HeldUids = ["1.1"];
    private static readonly string[] ListedUids = ["2.1"];

    private readonly BridgeManager _sut = new(NullLogger.Instance);

    private static BridgeSnapshotEntry Listed(string id) => new(id, "basic", "simple_bridge", null, null);

    private static Dictionary<string, IReadOnlyCollection<string>> Members(params (string Bridge, string[] Uids)[] members) =>
        members.ToDictionary(m => m.Bridge, m => (IReadOnlyCollection<string>)m.Uids);

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepABridgeCreatedAfterTheRequest_WhenTheSnapshotOmitsIt()
    {
        using var window = _sut.OpenSnapshotWindow();
        _sut.OnBridgeCreated("newer", "basic", "simple_bridge", null, null);
        using var counters = new LiveCounters();
        var destroyed = new List<string>();
        _sut.BridgeDestroyed += b => destroyed.Add(b.BridgeUniqueid);

        _sut.ReconcileWithSnapshot(window, [], Members(), removeOmitted: true);

        new { Held = _sut.GetById("newer")?.DestroyedAt is null, _sut.ActiveBridgeCount, Destroyed = destroyed, counters.BridgesDestroyed }
            .Should().BeEquivalentTo(new { Held = true, ActiveBridgeCount = 1, Destroyed = Array.Empty<string>(), BridgesDestroyed = 0L },
                "the bridge was created after the snapshot was requested, so the snapshot's silence about it is no evidence");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldNotBringBackABridgeDestroyedAfterTheRequest_WhenTheSnapshotStillListsIt()
    {
        _sut.OnBridgeCreated("gone", "basic", "simple_bridge", null, null);
        _sut.OnChannelEntered("gone", "1.1");
        using var window = _sut.OpenSnapshotWindow();
        _sut.OnBridgeDestroyed("gone");
        // Released at once, so the table no longer holds even the destroyed entry.
        _sut.DestroyedRetention = TimeSpan.Zero;
        _sut.OnBridgeCreated("other", "basic", "simple_bridge", null, null);
        using var counters = new LiveCounters();

        _sut.ReconcileWithSnapshot(window, [Listed("gone"), Listed("other")], Members(("gone", ["1.1"])), removeOmitted: true);

        new { Gone = _sut.GetById("gone"), _sut.ActiveBridgeCount, counters.BridgesCreated }
            .Should().BeEquivalentTo(new { Gone = default(AsteriskBridge), ActiveBridgeCount = 1, BridgesCreated = 0L },
                "Asterisk answered before the BridgeDestroy the SDK saw after the request: the listing does not bring it back");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldNotAdmitABridgeDestroyedDuringTheRead_WhenTheTableNeverHeldIt()
    {
        using var window = _sut.OpenSnapshotWindow();
        _sut.OnBridgeDestroyed("unseen");

        _sut.ReconcileWithSnapshot(window, [Listed("unseen")], Members(), removeOmitted: true);

        _sut.GetById("unseen").Should().BeNull("its destruction after the request is recorded even for a bridge never held");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepAChannelThatEnteredAfterTheRequestAndNotReAddOneThatLeft_WhenTheSnapshotSaysOtherwise()
    {
        _sut.OnBridgeCreated("b", "basic", "simple_bridge", null, null);
        _sut.OnChannelEntered("b", "left.1");
        using var window = _sut.OpenSnapshotWindow();
        _sut.OnChannelEntered("b", "entered.2");
        _sut.OnChannelLeft("b", "left.1");

        _sut.ReconcileWithSnapshot(window, [Listed("b")], Members(("b", ["left.1"])), removeOmitted: true);

        _sut.GetById("b")!.Channels.Keys.Should().BeEquivalentTo(["entered.2"],
            "the channel that entered after the request is newer than the snapshot, and the one that left after it is not "
            + "brought back by a snapshot taken before its leave");
        _sut.GetBridgeForChannel("left.1").Should().BeNull();
        _sut.GetBridgeForChannel("entered.2")!.BridgeUniqueid.Should().Be("b");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldAdmitButRemoveNothing_WhenTheAnswersCompletionCannotBeTold()
    {
        _sut.OnBridgeCreated("held", "basic", "simple_bridge", null, null);
        _sut.OnChannelEntered("held", "1.1");
        using var window = _sut.OpenSnapshotWindow();
        using var counters = new LiveCounters();
        var created = 0;
        var entered = 0;
        _sut.BridgeCreated += _ => created++;
        _sut.ChannelEntered += (_, _) => entered++;

        _sut.ReconcileWithSnapshot(window, [Listed("listed")], Members(("listed", ["2.1"])), removeOmitted: false);

        new
        {
            HeldKept = _sut.GetById("held")?.DestroyedAt is null,
            HeldMembers = _sut.GetById("held")!.Channels.Keys.ToArray(),
            ListedMembers = _sut.GetById("listed")?.Channels.Keys.ToArray(),
            _sut.ActiveBridgeCount,
            _sut.BridgeCount,
            counters.BridgesCreated,
            counters.BridgesDestroyed,
            Created = created,
            Entered = entered,
        }.Should().BeEquivalentTo(new
        {
            HeldKept = true,
            HeldMembers = HeldUids,
            ListedMembers = ListedUids,
            ActiveBridgeCount = 2,
            BridgeCount = 2,
            BridgesCreated = 1L,
            BridgesDestroyed = 0L,
            Created = 0,
            Entered = 0,
        }, "only the non-destructive half applies: the listed bridge is inserted silently and counted, and nothing is removed");
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldCountTheDestructionOnce_WhenAsteriskRepeatsItInsideTheRetention()
    {
        _sut.OnBridgeCreated("b", "basic", "simple_bridge", null, null);
        using var counters = new LiveCounters();
        var raised = 0;
        _sut.BridgeDestroyed += _ => raised++;

        _sut.OnBridgeDestroyed("b");
        _sut.OnBridgeDestroyed("b");

        new { counters.BridgesDestroyed, _sut.ActiveBridgeCount, Raised = raised }
            .Should().BeEquivalentTo(new { BridgesDestroyed = 1L, ActiveBridgeCount = 0, Raised = 2 },
                "live.bridges.destroyed follows the table's transitions: one bridge left the table once");
    }
}
