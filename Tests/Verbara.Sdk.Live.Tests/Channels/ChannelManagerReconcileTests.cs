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

    /// <summary>
    /// Reconcile the way the reload does: the admission mark is captured first, then the snapshot
    /// is taken. Everything the test arranged before this call is therefore at or below the mark
    /// and is judged by the snapshot; only a channel admitted <c>after</c> it is newer than the
    /// snapshot. The tests that bind that window pass their own mark instead (ADR-0062, design D6).
    /// </summary>
    private void ReconcileAgainst(params ChannelSnapshotEntry[] snapshot) =>
        _sut.ReconcileWithSnapshot(snapshot, _sut.CaptureAdmissionMark());

    // --- added only -------------------------------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldAdmitTheChannelAndRaiseChannelAdded_WhenTheSnapshotHoldsOneNotTracked()
    {
        ReconcileAgainst([Entry("1700000000.1", "PJSIP/2000-0001", ChannelState.Ringing)]);

        _sut.ChannelCount.Should().Be(1);
        _sut.GetByUniqueId("1700000000.1").Should().NotBeNull();
        _sut.GetByName("PJSIP/2000-0001").Should().NotBeNull();
        _added.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
        _removed.Should().BeEmpty("nothing was held, so nothing can have gone away");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldCarryTheSnapshotsCorrelationIdentifier_WhenAdmittingANewChannel()
    {
        ReconcileAgainst([Entry("1700000000.1", "PJSIP/2000-0001", linkedId: "1700000000.0")]);

        _sut.GetByUniqueId("1700000000.1")!.LinkedId.Should().Be("1700000000.0",
            "one call is one call across the reload; the snapshot's Linkedid is what says so");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldBehaveAsAPlainLoad_WhenNothingIsHeldYet()
    {
        ReconcileAgainst(
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

        ReconcileAgainst([Entry("1700000000.9", "PJSIP/9000-0009")]);

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

        ReconcileAgainst([]);

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

        ReconcileAgainst([]);

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

        ReconcileAgainst(
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

        ReconcileAgainst(
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
    public void ReconcileWithSnapshot_ShouldKeepTheStateItLastObservedLive_WhenTheSnapshotReportsADifferentOne()
    {
        // The decision design D5 left open once the reload started reading a real state header, and
        // the answer this method keeps: a held channel is NOT refreshed from the snapshot.
        //
        // The snapshot is older than every event that arrived while it was being read, and the
        // admission mark orders admissions only — nothing here can tell a snapshot state from a
        // NewState that overtook it. Refreshing would let a reload push a call that answered during
        // the read back to Ringing, which is D6's failure applied to state instead of existence.
        // The price is staleness: a state change missed during the outage is never corrected. What
        // is held is always a state Asterisk really reported, never an invented one.
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _added.Clear();

        ReconcileAgainst([Entry("1700000000.1", "PJSIP/2000-0001", ChannelState.Ringing)]);

        _sut.GetByUniqueId("1700000000.1")!.State.Should().Be(ChannelState.Up,
            "the held channel keeps the last state the SDK observed live; a snapshot read before "
            + "that observation must not roll it back");
        _stateChanged.Should().BeEmpty(
            "nothing was announced, because nothing in the table changed");
        _added.Should().BeEmpty();
        _removed.Should().BeEmpty();
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

        ReconcileAgainst(
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
        ReconcileAgainst([Entry("1700000000.9", "PJSIP/2000-0001")]);

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

        ReconcileAgainst([Entry("1700000000.2", "PJSIP/2000-0001")]);

        _removed.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
        _sut.GetByName("PJSIP/2000-0001").Should().NotBeNull(
            "the departing channel may only drop the name-index entry while it still points at " +
            "itself, or it evicts the survivor's entry");
        _sut.GetByName("PJSIP/2000-0001")!.UniqueId.Should().Be("1700000000.2");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldAdmitTheChannelOnce_WhenTheSnapshotRepeatsAnEntry()
    {
        ReconcileAgainst(
        [
            Entry("1700000000.1", "PJSIP/2000-0001"),
            Entry("1700000000.1", "PJSIP/2000-0001"),
        ]);

        _sut.ChannelCount.Should().Be(1);
        _added.Should().ContainSingle();
    }

    // --- the admission window (design D6) -----------------------------------------------------

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepTheChannelAndAnnounceNothing_WhenItWasAdmittedAfterTheSnapshotMark()
    {
        // The reload reads the mark, then asks Asterisk for its snapshot. The call below starts
        // while that request is in flight: it is in the table and it cannot be in the snapshot.
        var admittedThrough = _sut.CaptureAdmissionMark();
        _sut.OnNewChannel("1700000000.5", "PJSIP/2000-0005", ChannelState.Up);
        _added.Clear();

        _sut.ReconcileWithSnapshot([], admittedThrough);

        _sut.GetByUniqueId("1700000000.5").Should().NotBeNull(
            "the snapshot was requested before this channel existed, so it could not have reported "
            + "it; removing it here would end a call that is up");
        _sut.GetByName("PJSIP/2000-0005").Should().NotBeNull("the name index goes with the channel");
        _removed.Should().BeEmpty(
            "silence from a snapshot older than the channel is not evidence that the channel is gone");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldStillRemoveTheChannel_WhenItWasAdmittedBeforeTheSnapshotMark()
    {
        // The ordinary stale channel: held before the reload began, and absent from a snapshot that
        // completed. The window must not have been closed by weakening this.
        //
        // Its stamp is deliberately *strictly* below the mark — a second channel is admitted before
        // the mark is read — so this test discriminates the comparison rather than resting on the
        // equal case, which `>` and `<` both decide the same way.
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        _sut.OnNewChannel("1700000000.2", "PJSIP/3000-0002", ChannelState.Up);
        var admittedThrough = _sut.CaptureAdmissionMark();
        _added.Clear();

        _sut.ReconcileWithSnapshot([Entry("1700000000.2", "PJSIP/3000-0002")], admittedThrough);

        _sut.GetByUniqueId("1700000000.1").Should().BeNull(
            "the snapshot could have reported this channel and did not; that absence is the only "
            + "notification the consumer will ever get that the call ended");
        _removed.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.1");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldJudgeTheOlderChannelAndSpareTheNewerOne_WhenTheMarkFallsBetweenThem()
    {
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        var admittedThrough = _sut.CaptureAdmissionMark();
        _sut.OnNewChannel("1700000000.5", "PJSIP/2000-0005", ChannelState.Up);
        _added.Clear();

        _sut.ReconcileWithSnapshot([], admittedThrough);

        _removed.Should().ContainSingle(
            "exactly one of the two is older than the snapshot").Which.UniqueId.Should()
            .Be("1700000000.1");
        _sut.ActiveChannels.Select(c => c.UniqueId).Should().BeEquivalentTo(["1700000000.5"]);
    }

    [Fact]
    public void OnNewChannel_ShouldStampEachAdmissionAboveTheMarkReadBeforeIt_WhenChannelsArriveInOrder()
    {
        var beforeAny = _sut.CaptureAdmissionMark();
        _sut.OnNewChannel("1700000000.1", "PJSIP/2000-0001", ChannelState.Up);
        var afterFirst = _sut.CaptureAdmissionMark();
        _sut.OnNewChannel("1700000000.2", "PJSIP/3000-0002", ChannelState.Up);

        // The counter, not the clock: two admissions this close together share a CreatedAt on a
        // coarse timer, which is why design D6 rejected filtering on it.
        _sut.GetByUniqueId("1700000000.1")!.AdmissionMark.Should().BeGreaterThan(beforeAny);
        _sut.GetByUniqueId("1700000000.2")!.AdmissionMark.Should().BeGreaterThan(afterFirst);
        _sut.GetByUniqueId("1700000000.2")!.AdmissionMark.Should()
            .BeGreaterThan(_sut.GetByUniqueId("1700000000.1")!.AdmissionMark);
    }

    [Fact]
    public async Task CaptureAdmissionMark_ShouldStampEveryChannelExactlyOnce_WhenAdmissionsRaceEachOther()
    {
        // Channels arrive on the AMI observer thread while a reload reads its snapshot, so the
        // counter has to be interlocked: a lost increment would give two channels one stamp and
        // make the comparison decide the same way for both.
        const int admissions = 256;

        // Its own manager, with nothing subscribed: the shared _added list is a plain List<T> and
        // concurrent ChannelAdded handlers would corrupt it, which would fail this test for a
        // reason that has nothing to do with the counter.
        var manager = new ChannelManager(NullLogger.Instance);
        var before = manager.CaptureAdmissionMark();

        await Parallel.ForAsync(0, admissions, (i, _) =>
        {
            manager.OnNewChannel($"1700000000.{i}", $"PJSIP/2000-{i}", ChannelState.Up);
            return ValueTask.CompletedTask;
        });

        var marks = manager.ActiveChannels.Select(c => c.AdmissionMark).ToList();
        marks.Should().HaveCount(admissions);
        marks.Distinct().Should().HaveCount(admissions, "a lost increment is a duplicated stamp");
        marks.Should().OnlyContain(m => m > before);
        manager.CaptureAdmissionMark().Should().Be(before + admissions);
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
