using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Live.Tests.MeetMe;

/// <summary>
/// A captured conference row cannot reverse a live report received during its read window. Mute and talking
/// reports are independent, including reports received before a participant can be admitted from that row.
/// </summary>
public sealed class ConferenceSnapshotStateOrderingTests : IDisposable
{
    private const string Room = "100";
    private const string ChannelA = "Local/conf@probe-00000000;2";
    private const string ChannelB = "Local/conf@probe-00000002;2";
    private const string ChannelC = "Local/conf@probe-00000004;2";

    private readonly MeetMeManager _sut = new(NullLogger.Instance);
    private readonly List<ConferenceSnapshotWindow> _windows = [];
    private readonly List<Observed> _changed = [];
    private readonly List<MeetMeUser> _joined = [];
    private readonly List<MeetMeUser> _left = [];

    public ConferenceSnapshotStateOrderingTests()
    {
        _sut.UserStateChanged += ObserveChanged;
        _sut.UserJoined += ObserveJoined;
        _sut.UserLeft += ObserveLeft;
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            window.Dispose();
        _sut.ActiveSnapshotWindowCount.Should().Be(0, "the fixture releases every read window, including after a failed assertion");
    }

    [Theory]
    [InlineData("Muted", true)]
    [InlineData("Muted", false)]
    [InlineData("Talking", true)]
    [InlineData("Talking", false)]
    public void ReconcileWithSnapshot_ShouldKeepTheNewerLiveValueAndEmitNoReversal_WhenAStateEventFollowsTheCapturedRow(
        string field, bool liveValue)
    {
        var held = JoinWithField(field, !liveValue);
        var window = OpenWindow();
        var row = FieldRow(field, !liveValue);
        ClearObserved();

        Report(field, liveValue);
        Apply(window, row);

        using var scope = new AssertionScope();
        Participant().Should().BeSameAs(held, "a state refresh preserves the held participant's identity");
        Value(held, field).Should().Be(liveValue,
            "the captured row predates the live report received while the read window was open");
        _changed.Should().Equal([StateWith(field, liveValue)],
            "only the actual live change is announced; applying its older row must not emit a reversal");
        _joined.Should().BeEmpty();
        _left.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Muted", true)]
    [InlineData("Muted", false)]
    [InlineData("Talking", true)]
    [InlineData("Talking", false)]
    public void ReconcileWithSnapshot_ShouldProtectTheReportedFieldWithoutRaisingAChange_WhenTheLiveValueAlreadyMatches(
        string field, bool liveValue)
    {
        var held = JoinWithField(field, liveValue);
        var window = OpenWindow();
        var row = FieldRow(field, !liveValue);
        ClearObserved();

        Report(field, liveValue);
        Apply(window, row);

        using var scope = new AssertionScope();
        Participant().Should().BeSameAs(held);
        Value(held, field).Should().Be(liveValue,
            "a same-value live report is still newer than the buffered snapshot");
        _changed.Should().BeEmpty("neither the repeated live value nor a skipped stale field is a change");
    }

    [Theory]
    [InlineData("Muted")]
    [InlineData("Talking")]
    public void ReconcileWithSnapshot_ShouldRefreshTheUntouchedOtherField_WhenOnlyOneFieldHasANewerLiveReport(string field)
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var window = OpenWindow();
        var row = Row(ChannelA, muted: true, talking: true);
        ClearObserved();

        Report(field, false);
        Apply(window, row);

        var expected = new Observed(ChannelA, Muted: field != "Muted", Talking: field != "Talking");
        using var scope = new AssertionScope();
        Observe(Participant()).Should().Be(expected,
            "the same-value live report protects only its own field; the other readable field still refreshes");
        _changed.Should().Equal([expected], "only the untouched field's snapshot change is announced");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldRefreshOtherParticipants_WhenOneParticipantHasANewerLiveReport()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        _sut.OnUserJoined(Room, 0, ChannelB, "b");
        var window = OpenWindow();
        var rows = new[] { Row(ChannelA), Row(ChannelB, muted: true, talking: true) };
        ClearObserved();

        _sut.OnUserMuted(Room, ChannelA, true);
        Apply(window, rows);

        using var scope = new AssertionScope();
        Observe(Participant()).Should().Be(new Observed(ChannelA, true, false));
        Observe(Participant(ChannelB)).Should().Be(new Observed(ChannelB, true, true),
            "the other participant has no newer live field and remains refreshable");
        _changed.Where(state => state.Channel == ChannelA).Should().Equal([new Observed(ChannelA, true, false)],
            "the first participant's stale row emits no reversal");
        _changed.Where(state => state.Channel == ChannelB).Should().HaveCount(2,
            "the second participant's two readable fields both really change");
    }

    [Theory]
    [InlineData("Muted", true, false)]
    [InlineData("Muted", true, true)]
    [InlineData("Muted", false, false)]
    [InlineData("Muted", false, true)]
    [InlineData("Talking", true, false)]
    [InlineData("Talking", true, true)]
    [InlineData("Talking", false, false)]
    [InlineData("Talking", false, true)]
    public void ReconcileWithSnapshot_ShouldSeedTheLatestLiveFieldSilently_WhenItsReportArrivesBeforeSnapshotAdmission(
        string field, bool roomHeld, bool liveValue)
    {
        if (roomHeld)
            _sut.OnUserJoined(Room, 0, ChannelB, "b");
        var window = OpenWindow();
        var row = field == "Muted"
            ? Row(ChannelA, muted: !liveValue, talking: true)
            : Row(ChannelA, muted: true, talking: !liveValue);
        ClearObserved();

        Report(field, liveValue);
        var existedBeforeAdmission = _sut.GetRoom(Room)?.Users.ContainsKey(ChannelA) == true;
        var roomExistedBeforeAdmission = _sut.GetRoom(Room) is not null;
        var announcedBeforeAdmission = _changed.Count + _joined.Count;
        Apply(window, row, Row(ChannelB, muted: null, talking: null));

        var admitted = Participant();
        using var scope = new AssertionScope();
        existedBeforeAdmission.Should().BeFalse("an absent-key state report alone admits no participant");
        roomExistedBeforeAdmission.Should().Be(roomHeld, "a state report alone creates no room");
        announcedBeforeAdmission.Should().Be(0);
        Observe(admitted).Should().Be(new Observed(ChannelA,
            Muted: field != "Muted" || liveValue,
            Talking: field != "Talking" || liveValue),
            "admission uses the newer absent-key report for its field and the row for the untouched other field");
        admitted.UniqueId.Should().Be($"uid-{ChannelA}");
        _joined.Should().BeEmpty("a snapshot admission is silent even when it merges a live field");
        _changed.Should().BeEmpty("seeding an absent participant is not a synthetic state change or reversal");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepTheNewJoinIdentityAndDefaults_WhenTheOldRowListsTheSameChannel()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "old");
        var old = Participant();
        var window = OpenWindow();
        var row = Row(ChannelA, muted: true, talking: true);
        _sut.OnUserJoined(Room, 0, ChannelA, "new");
        var joined = Participant();
        ClearObserved();

        Apply(window, row);

        using var scope = new AssertionScope();
        Participant().Should().BeSameAs(joined).And.NotBeSameAs(old);
        joined.UniqueId.Should().Be("new");
        Observe(joined).Should().Be(new Observed(ChannelA, false, false),
            "the later join started a new participant; its fields cannot come from the older lifetime's row");
        _changed.Should().BeEmpty();
        _joined.Should().BeEmpty("the live join was already delivered before reconciliation");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepTheRejoinedParticipant_WhenTheOldLifetimeHadLiveFieldReports()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "old");
        var window = OpenWindow();
        var row = Row(ChannelA, muted: true, talking: true);
        _sut.OnUserMuted(Room, ChannelA, true);
        _sut.OnUserTalking(Room, ChannelA, true);
        _sut.OnUserLeft(Room, ChannelA);
        _sut.OnUserJoined(Room, 0, ChannelA, "new");
        var joined = Participant();
        ClearObserved();

        Apply(window, row);

        using var scope = new AssertionScope();
        Participant().Should().BeSameAs(joined);
        joined.UniqueId.Should().Be("new");
        Observe(joined).Should().Be(new Observed(ChannelA, false, false));
        _changed.Should().BeEmpty("prior-life live field reports do not transfer through leave/rejoin");
        _joined.Should().BeEmpty();
        _left.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldResetAbsentKeyReports_WhenALeaveStartsAnotherLifetime()
    {
        var old = OpenWindow();
        _sut.OnUserMuted(Room, ChannelA, true);
        _sut.OnUserTalking(Room, ChannelA, true);
        _sut.OnUserLeft(Room, ChannelA);
        var fresh = OpenWindow();

        Apply(old, Row(ChannelA, muted: true, talking: true));
        var admittedByOld = _sut.GetRoom(Room)?.Users.ContainsKey(ChannelA) == true;
        Apply(fresh, Row(ChannelA));

        using var scope = new AssertionScope();
        admittedByOld.Should().BeFalse("the leave invalidates rows already being read even for an absent key");
        Observe(Participant()).Should().Be(new Observed(ChannelA, false, false));
        _changed.Should().BeEmpty();
        _joined.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconcileWithSnapshot_ShouldKeepTheOldRoomRowsInvalidAfterAJoin_WhenTheRoomEndedDuringTheRead(bool roomHeld)
    {
        if (roomHeld)
            _sut.OnUserJoined(Room, 0, ChannelA, "old");
        var window = OpenWindow();
        var rows = new[] { Row(ChannelA, true, true), Row(ChannelB, true, true), Row(ChannelC, true, true) };
        _sut.OnUserMuted(Room, ChannelB, true);
        _sut.OnUserTalking(Room, ChannelB, true);
        _sut.OnConferenceEnded(Room);
        _sut.OnUserJoined(Room, 0, ChannelC, "new");
        var joined = Participant(ChannelC);
        ClearObserved();

        Apply(window, rows);

        using var scope = new AssertionScope();
        _sut.GetRoom(Room)!.Users.Keys.Should().Equal([ChannelC],
            "a join starts a new room lifetime but does not permit old-window rows, including never-held channels");
        Participant(ChannelC).Should().BeSameAs(joined);
        Observe(joined).Should().Be(new Observed(ChannelC, false, false));
        _changed.Should().BeEmpty();
        _joined.Should().BeEmpty();
        _left.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldAllowNewRoomRows_WhenItsWindowOpenedAfterTheRoomEnded()
    {
        var old = OpenWindow();
        _sut.OnUserMuted(Room, ChannelA, true);
        _sut.OnUserTalking(Room, ChannelA, true);
        _sut.OnConferenceEnded(Room);
        _sut.OnUserJoined(Room, 0, ChannelC, "new");
        var fresh = OpenWindow();
        ClearObserved();

        Apply(fresh, Row(ChannelA), Row(ChannelB, true, true), Row(ChannelC));

        using var scope = new AssertionScope();
        _sut.GetRoom(Room)!.Users.Keys.Should().BeEquivalentTo([ChannelA, ChannelB, ChannelC]);
        Observe(Participant()).Should().Be(new Observed(ChannelA, false, false),
            "the ended room's earlier absent-key field reports do not seed its new lifetime");
        Observe(Participant(ChannelB)).Should().Be(new Observed(ChannelB, true, true));
        _joined.Should().BeEmpty();
        _changed.Should().BeEmpty();
        old.IsClosed.Should().BeFalse("the fresh window is independent of the still-open older one");
    }

    [Theory]
    [InlineData("Muted")]
    [InlineData("Talking")]
    public void ReconcileWithSnapshot_ShouldCompareEachWindowWithItsOwnOrderPoint_WhenWindowsOverlapAStateReport(string field)
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var old = OpenWindow();
        var row = FieldRow(field, false);
        Report(field, true);
        var fresh = OpenWindow();
        ClearObserved();

        Apply(old, row);
        var afterOld = Value(Participant(), field);
        var changesAfterOld = _changed.ToArray();
        Apply(fresh, row);

        using var scope = new AssertionScope();
        afterOld.Should().BeTrue("the older window predates the live report");
        changesAfterOld.Should().BeEmpty("the old row must not reverse the newer report");
        Value(Participant(), field).Should().BeFalse("the fresh window may refresh a field last reported before it opened");
        _changed.Should().Equal([StateWith(field, false)]);
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepOtherWindowsRefreshable_WhenAFieldWasUpdatedOnlyByASnapshot()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var first = OpenWindow();
        var second = OpenWindow();
        ClearObserved();

        Apply(first, Row(ChannelA, true, true));
        Apply(second, Row(ChannelA));

        using var scope = new AssertionScope();
        Observe(Participant()).Should().Be(new Observed(ChannelA, false, false),
            "a snapshot refresh is not a live mutation stamped against another open window");
        _changed.Should().HaveCount(4, "both snapshots changed both readable fields");
    }

    [Theory]
    [InlineData("Muted")]
    [InlineData("Talking")]
    public void ReconcileWithSnapshot_ShouldKeepTheNewerLiveField_WhenTheWindowAllowsAdmissionsOnly(string field)
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var window = OpenWindow();
        var row = FieldRow(field, false);
        ClearObserved();
        Report(field, true);

        _sut.ReconcileWithSnapshot(window, [Room], new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>
        {
            [Room] = [row],
        }, removeOmitted: false);

        using var scope = new AssertionScope();
        Value(Participant(), field).Should().BeTrue();
        _changed.Should().Equal([StateWith(field, true)],
            "admission-only reconciliation is not permission to reverse a newer live field");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepHeldFields_WhenTheRowCarriesUnknownValues()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        _sut.OnUserMuted(Room, ChannelA, true);
        _sut.OnUserTalking(Room, ChannelA, true);
        var held = Participant();
        var window = OpenWindow();
        ClearObserved();

        Apply(window, Row(ChannelA, muted: null, talking: null));

        using var scope = new AssertionScope();
        Participant().Should().BeSameAs(held);
        Observe(held).Should().Be(new Observed(ChannelA, true, true));
        _changed.Should().BeEmpty("unknown fields do not overwrite readable held values");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldDefaultBothFieldsSilently_WhenANewParticipantHasUnknownRowValues()
    {
        var window = OpenWindow();

        Apply(window, Row(ChannelA, muted: null, talking: null));

        using var scope = new AssertionScope();
        Observe(Participant()).Should().Be(new Observed(ChannelA, false, false));
        _changed.Should().BeEmpty();
        _joined.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Muted")]
    [InlineData("Talking")]
    public void ReconcileWithSnapshot_ShouldPreferTheAbsentKeyLiveReportToAnUnknownDefault_WhenTheRowHasNoReadableState(string field)
    {
        var window = OpenWindow();
        var row = Row(ChannelA, muted: null, talking: null);
        Report(field, true);

        Apply(window, row);

        using var scope = new AssertionScope();
        Observe(Participant()).Should().Be(StateWith(field, true),
            "a readable newer live report seeds its field even when the row would default that field to false");
        _changed.Should().BeEmpty();
        _joined.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepTheReentrantLiveTalkingValue_WhenAMuteRefreshSubscriberReportsTalking()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var held = Participant();
        var window = OpenWindow();
        var row = Row(ChannelA, muted: true, talking: false);
        var injected = false;
        _sut.UserStateChanged += (_, user) =>
        {
            if (injected || !user.Muted)
                return;
            injected = true;
            _sut.OnUserTalking(Room, ChannelA, true);
        };
        ClearObserved();

        Apply(window, row);

        using var scope = new AssertionScope();
        injected.Should().BeTrue("the live report enters through the state subscriber during reconciliation");
        Participant().Should().BeSameAs(held);
        Observe(held).Should().Be(new Observed(ChannelA, true, true));
        _changed.Should().Equal([new Observed(ChannelA, true, false), new Observed(ChannelA, true, true)],
            "the snapshot mute change and newer live talking change are real; no stale talking reversal follows");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldApplyTheWholeReadBeforeAStateSubscriberClosesItsWindow_WhenTheSubscriberClosesIt()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var held = Participant();
        var window = OpenWindow();
        var openWhenRaised = new List<bool>();
        _sut.UserStateChanged += (_, _) =>
        {
            openWhenRaised.Add(!window.IsClosed);
            window.Dispose();
        };
        ClearObserved();

        Apply(window, Row(ChannelA, true, true), Row(ChannelB, true, true));

        using var scope = new AssertionScope();
        window.IsClosed.Should().BeTrue("the callback closes the actual read once the reconciliation released the gate");
        _sut.ActiveSnapshotWindowCount.Should().Be(0);
        Observe(held).Should().Be(new Observed(ChannelA, true, true),
            "the reconciliation is one critical section: no handler runs inside it");
        _sut.GetRoom(Room)!.Users.Should().ContainKey(ChannelB);
        openWhenRaised.Should().Equal([true, false], "the first notification is raised after the whole read was applied");
        _changed.Should().Equal([new Observed(ChannelA, true, true), new Observed(ChannelA, true, true)],
            "one notification per changed field, each reading the participant as it is when raised");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldPreserveTheReplacement_WhenAStateSubscriberRejoinsTheChannel()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "old");
        var old = Participant();
        var window = OpenWindow();
        _sut.UserStateChanged += (_, _) => _sut.OnUserJoined(Room, 0, ChannelA, "new");
        ClearObserved();

        Apply(window, Row(ChannelA, true, true));

        var replacement = Participant();
        using var scope = new AssertionScope();
        replacement.Should().NotBeSameAs(old);
        replacement.UniqueId.Should().Be("new");
        Observe(replacement).Should().Be(new Observed(ChannelA, false, false),
            "no snapshot field reaches the participant a subscriber's rejoin installed");
        Observe(old).Should().Be(new Observed(ChannelA, true, true),
            "both fields were applied to the participant held during the reconciliation, before any handler ran");
        _changed.Should().Equal([new Observed(ChannelA, true, true), new Observed(ChannelA, true, true)]);
        _joined.Should().HaveCount(2, "each changed field's notification runs the subscriber's rejoin once");
        _joined[^1].Should().BeSameAs(replacement);
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldRejectAClosedWindowAndAllowAFreshRead_WhenTheReadIsDisposedTwice()
    {
        using var closed = OpenWindow();
        _sut.OnUserMuted(Room, ChannelA, true);
        _sut.OnUserTalking(Room, ChannelA, true);
        closed.Dispose();
        closed.Dispose();

        var rejected = () => Apply(closed, Row(ChannelA, true, true));
        rejected.Should().Throw<ObjectDisposedException>();
        var windowsAfterClose = _sut.ActiveSnapshotWindowCount;
        var fresh = OpenWindow();
        Apply(fresh, Row(ChannelA));

        using var scope = new AssertionScope();
        closed.IsClosed.Should().BeTrue();
        windowsAfterClose.Should().Be(0, "disposing twice releases the one read-window ownership exactly once");
        Observe(Participant()).Should().Be(new Observed(ChannelA, false, false));
        _joined.Should().BeEmpty();
        _changed.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldAllowAnIndependentFreshRead_WhenTheEarlierReadWasCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        ConferenceSnapshotWindow? cancelled = null;
        Action read = () =>
        {
            using var window = OpenWindow();
            cancelled = window;
            _sut.OnUserMuted(Room, ChannelA, true);
            _sut.OnUserTalking(Room, ChannelA, true);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        read.Should().Throw<OperationCanceledException>();
        var windowsAfterCancel = _sut.ActiveSnapshotWindowCount;
        var fresh = OpenWindow();
        Apply(fresh, Row(ChannelA));

        using var scope = new AssertionScope();
        cancelled.Should().NotBeNull();
        cancelled!.IsClosed.Should().BeTrue("the abandoned read closes its real manager window on cancellation");
        windowsAfterCancel.Should().Be(0, "a cancelled read leaves no open-window ownership behind");
        Observe(Participant()).Should().Be(new Observed(ChannelA, false, false));
        _changed.Should().BeEmpty();
        _joined.Should().BeEmpty();
    }

    private ConferenceSnapshotWindow OpenWindow()
    {
        var window = _sut.OpenSnapshotWindow();
        _windows.Add(window);
        return window;
    }

    private MeetMeUser JoinWithField(string field, bool value)
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        Report(field, value);
        return Participant();
    }

    private void Report(string field, bool value)
    {
        if (field == "Muted")
            _sut.OnUserMuted(Room, ChannelA, value);
        else
            _sut.OnUserTalking(Room, ChannelA, value);
    }

    private void Apply(ConferenceSnapshotWindow window, params ConferenceSnapshotParticipant[] rows) =>
        _sut.ReconcileWithSnapshot(window, [Room], new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>
        {
            [Room] = rows,
        }, removeOmitted: true);

    private static ConferenceSnapshotParticipant Row(string channel, bool? muted = false, bool? talking = false) =>
        new(channel, $"uid-{channel}", muted, talking);

    private static ConferenceSnapshotParticipant FieldRow(string field, bool value) =>
        field == "Muted" ? Row(ChannelA, muted: value, talking: null) : Row(ChannelA, muted: null, talking: value);

    private static bool Value(MeetMeUser user, string field) => field == "Muted" ? user.Muted : user.Talking;

    private static Observed StateWith(string field, bool value) =>
        new(ChannelA, Muted: field == "Muted" && value, Talking: field == "Talking" && value);

    private MeetMeUser Participant(string channel = ChannelA) => _sut.GetRoom(Room)!.Users[channel];

    private static Observed Observe(MeetMeUser user) => new(user.Channel, user.Muted, user.Talking);

    private void ObserveChanged(MeetMeRoom _, MeetMeUser user) => _changed.Add(Observe(user));

    private void ObserveJoined(MeetMeRoom _, MeetMeUser user) => _joined.Add(user);

    private void ObserveLeft(MeetMeRoom _, MeetMeUser user) => _left.Add(user);

    private void ClearObserved()
    {
        _changed.Clear();
        _joined.Clear();
        _left.Clear();
    }

    private sealed record Observed(string Channel, bool Muted, bool Talking);
}
