using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// Unit coverage for <c>ChannelManager.ReconcileWithSnapshot</c> — the reconcile entry point the
/// post-reconnect reload uses instead of <c>Clear()</c> (ADR-0062, design D2/D3). These tests drive
/// the manager alone: no <c>VerbaraServer</c>, no AMI connection, no session manager.
/// </summary>
public class ChannelManagerReconcileTests
{
    private readonly ChannelManager _sut = new(NullLogger.Instance);
    private readonly List<AsteriskChannel> _added = [];
    private readonly List<AsteriskChannel> _removed = [];
    private readonly List<AsteriskChannel> _stateChanged = [];

    public ChannelManagerReconcileTests()
    {
        _sut.ChannelAdded += ch => _added.Add(ch);
        _sut.ChannelRemoved += ch => _removed.Add(ch);
        _sut.ChannelStateChanged += ch => _stateChanged.Add(ch);
    }

    private static ChannelSnapshotEntry Entry(string uniqueId, string name,
        ChannelState state = ChannelState.Up, string? linkedId = null, string? callerIdNum = null) =>
        new(uniqueId, name, state, CallerIdNum: callerIdNum, LinkedId: linkedId);

    // --- added only -------------------------------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldAdmitTheChannelAndRaiseChannelAdded_WhenTheSnapshotHoldsOneNotTracked()
    {
        _sut.ReconcileWithSnapshot([Entry("1700000000.1", "PJSIP/2000-0001", ChannelState.Ringing)]);

        _sut.ChannelCount.Should().Be(1);
        _sut.GetByUniqueId("1700000000.1").Should().NotBeNull();
        _sut.GetByName("PJSIP/2000-0001").Should().NotBeNull();
        _added.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
        _removed.Should().BeEmpty("nothing was held, so nothing can have gone away");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldCarryTheSnapshotsCorrelationIdentifier_WhenAdmittingANewChannel()
    {
        _sut.ReconcileWithSnapshot([Entry("1700000000.1", "PJSIP/2000-0001", linkedId: "1700000000.0")]);

        _sut.GetByUniqueId("1700000000.1")!.LinkedId.Should().Be("1700000000.0",
            "one call is one call across the reload; the snapshot's Linkedid is what says so");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldBehaveAsAPlainLoad_WhenNothingIsHeldYet()
    {
        _sut.ReconcileWithSnapshot(
        [
            Entry("1700000000.1", "PJSIP/2000-0001"),
            Entry("1700000000.2", "PJSIP/3000-0002"),
        ]);

        _sut.ChannelCount.Should().Be(2);
        _added.Should().HaveCount(2, "a diff against an empty table is exactly today's initial load");
        _removed.Should().BeEmpty();
    }

    // --- removed only -----------------------------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldRemoveTheChannelAndRaiseChannelRemoved_WhenTheSnapshotOmitsIt()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _added.Clear();

        _sut.ReconcileWithSnapshot([Entry("1700000000.9", "PJSIP/9000-0009")]);

        _sut.GetByUniqueId("1700000000.1").Should().BeNull();
        _sut.GetByName("PJSIP/2000-0001").Should().BeNull("the name index must go with the channel");
        _removed.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldRemoveEveryHeldChannel_WhenTheSnapshotIsEmpty()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _sut.OnNewChannel("1700000000.2", "PJSIP/3000-0002", ChannelState.Up);
        _added.Clear();

        _sut.ReconcileWithSnapshot([]);

        _sut.ChannelCount.Should().Be(0);
        _removed.Should().HaveCount(2,
            "Asterisk replays nothing after a reconnect, so an empty completed snapshot is the only " +
            "notification the consumer will ever get that those calls ended");
        _added.Should().BeEmpty();
    }

    // --- the seam task 2.4 consumes ----------------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldMarkTheRemovalAsComingFromAReload_WhenAHeldChannelIsAbsent()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);

        _sut.ReconcileWithSnapshot([]);

        var gone = _removed.Should().ContainSingle().Subject;
        gone.RemovedByReload.Should().BeTrue(
            "the ending must be distinguishable from an observed hangup");
        gone.HangupCause.Should().Be(HangupCause.NotDefined,
            "no cause was observed, so the reload attributes none; the marker — not cause zero — is " +
            "what tells a subscriber the cause is unknown");
        gone.State.Should().Be(ChannelState.Down);
    }

    [Fact]
    public void OnHangup_ShouldNotMarkTheRemovalAsComingFromAReload_WhenAHangupWasObserved()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);

        _sut.OnHangup("1700000000.1", HangupCause.UserBusy);

        var gone = _removed.Should().ContainSingle().Subject;
        gone.RemovedByReload.Should().BeFalse("a hangup was observed, so this ending has provenance");
        gone.HangupCause.Should().Be(HangupCause.UserBusy, "Asterisk's cause must survive unchanged");
    }

    // --- mixed ------------------------------------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldActOnlyOnTheDifference_WhenTheSnapshotOverlapsWhatIsHeld()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _sut.OnNewChannel("1700000000.2", "PJSIP/3000-0002", ChannelState.Up);
        _added.Clear();

        _sut.ReconcileWithSnapshot(
        [
            Entry("1700000000.2", "PJSIP/3000-0002"),
            Entry("1700000000.3", "PJSIP/4000-0003"),
        ]);

        _added.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.3");
        _removed.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
        _sut.ActiveChannels.Select(c => c.UniqueId).Should()
            .BeEquivalentTo(["1700000000.2", "1700000000.3"]);
    }

    // --- identical ---------------------------------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldRaiseNothingAtAll_WhenTheSnapshotMatchesWhatIsHeld()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _sut.OnNewChannel("1700000000.2", "PJSIP/3000-0002", ChannelState.Up);
        _added.Clear();

        _sut.ReconcileWithSnapshot(
        [
            Entry("1700000000.1", "PJSIP/2000-0001"),
            Entry("1700000000.2", "PJSIP/3000-0002"),
        ]);

        _added.Should().BeEmpty("a channel the snapshot still contains has not newly appeared");
        _removed.Should().BeEmpty("a channel the snapshot still contains has not gone away");
        _stateChanged.Should().BeEmpty("nothing changed, so nothing is announced");
        _sut.ChannelCount.Should().Be(2);
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepTheHeldInstanceAndItsRelationalState_WhenTheSnapshotStillContainsIt()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up, linkedId: "1700000000.0");
        _sut.OnNewChannel("1700000000.2", "PJSIP/3000-0002", ChannelState.Up, linkedId: "1700000000.0");
        _sut.OnLink("1700000000.1", "1700000000.2");
        _sut.OnHold("1700000000.1", "default");
        _sut.OnDialBegin("1700000000.1", "1700000000.2", "PJSIP/3000-0002", "3000");
        var before = _sut.GetByUniqueId("1700000000.1")!;
        var createdAt = before.CreatedAt;

        _sut.ReconcileWithSnapshot(
        [
            // The reload carries no correlation for this leg; the held instance already has it.
            Entry("1700000000.1", "PJSIP/2000-0001"),
            Entry("1700000000.2", "PJSIP/3000-0002"),
        ]);

        var after = _sut.GetByUniqueId("1700000000.1")!;
        after.Should().BeSameAs(before,
            "re-admitting a held channel through OnNewChannel would discard exactly this state");
        after.LinkedId.Should().Be("1700000000.0", "LinkedId is init-only and cannot be re-set in place");
        after.LinkedChannel.Should().BeSameAs(_sut.GetByUniqueId("1700000000.2"));
        after.IsOnHold.Should().BeTrue();
        after.DialedChannel.Should().Be("PJSIP/3000-0002");
        after.CreatedAt.Should().Be(createdAt);
    }

    // --- ordering ----------------------------------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldLeaveTheAdmittedChannelIndexedByName_WhenADepartedChannelHeldThatName()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _added.Clear();

        // Asterisk reused the name for a different channel while the link was down.
        _sut.ReconcileWithSnapshot([Entry("1700000000.9", "PJSIP/2000-0001")]);

        _sut.GetByName("PJSIP/2000-0001").Should().NotBeNull(
            "the departing channel must not evict the name-index entry the admitted channel just " +
            "took; removals run before admissions, and RemoveByReload drops the entry only while " +
            "it still points at the departing instance");
        _sut.GetByName("PJSIP/2000-0001")!.UniqueId.Should().Be("1700000000.9");
        _removed.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
        _added.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.9");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldLeaveTheSurvivorIndexedByName_WhenADepartedChannelSharedItsName()
    {
        // Two tracked channels can carry the same name: OnRename and OnNewChannel both write the
        // name index unconditionally, so a masquerade leaves a stale duplicate behind.
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _sut.OnNewChannel("1700000000.2", "PJSIP/2000-0001", ChannelState.Up);
        _added.Clear();

        _sut.ReconcileWithSnapshot([Entry("1700000000.2", "PJSIP/2000-0001")]);

        _removed.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
        _sut.GetByName("PJSIP/2000-0001").Should().NotBeNull(
            "the departing channel may only drop the name-index entry while it still points at " +
            "itself, or it evicts the survivor's entry");
        _sut.GetByName("PJSIP/2000-0001")!.UniqueId.Should().Be("1700000000.2");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldAdmitTheChannelOnce_WhenTheSnapshotRepeatsAnEntry()
    {
        _sut.ReconcileWithSnapshot(
        [
            Entry("1700000000.1", "PJSIP/2000-0001"),
            Entry("1700000000.1", "PJSIP/2000-0001"),
        ]);

        _sut.ChannelCount.Should().Be(1);
        _added.Should().ContainSingle();
    }

    // --- Clear() is untouched -----------------------------------------------------------------

    [Fact]
    public void Clear_ShouldStillEmptyTheTableWithoutAnnouncingAnything_WhenChannelsAreHeld()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _added.Clear();

        _sut.Clear();

        _sut.ChannelCount.Should().Be(0);
        _removed.Should().BeEmpty(
            "Clear() stays exactly as it is — this change removes one of its callers, not the method");
    }
}
