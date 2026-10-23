using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Live.Tests.MeetMe;

/// <summary>
/// The conference table's reconciliation against a load's listings holds to the read window: a participant that joined
/// after the room listing was requested is not removed, one that left after it is not brought back, a room whose
/// participant listing did not complete is left alone, an answer whose completion cannot be told only admits, and a
/// participant already held keeps its entry, refreshed only from a readable value when no live state report intervenes.
/// </summary>
public sealed class ConferenceSnapshotReconciliationTests
{
    private const string Room = "100";
    private const string ChannelA = "Local/conf@probe-00000000;2";
    private const string ChannelB = "Local/conf@probe-00000002;2";
    private const string ChannelC = "Local/conf@probe-00000004;2";

    private readonly MeetMeManager _sut = new(NullLogger.Instance);
    private readonly List<string> _left = [];
    private readonly List<string> _joined = [];

    public ConferenceSnapshotReconciliationTests()
    {
        _sut.UserLeft += (_, u) => _left.Add(u.Channel);
        _sut.UserJoined += (_, u) => _joined.Add(u.Channel);
    }

    private static ConferenceSnapshotParticipant Row(string channel, bool? muted = false, bool? talking = false) =>
        new(channel, $"uid-{channel}", muted, talking);

    private static Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>> Rows(params ConferenceSnapshotParticipant[] rows) =>
        new() { [Room] = rows };

    private string[] Participants() => _sut.GetRoom(Room)?.Users.Keys.Order().ToArray() ?? [];

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepAParticipantThatJoinedAfterTheRequest_WhenTheListingOmitsIt()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        using var window = _sut.OpenSnapshotWindow();
        _sut.OnUserJoined(Room, 0, ChannelB, "b");
        _joined.Clear();

        _sut.ReconcileWithSnapshot(window, [Room], Rows(Row(ChannelA)), removeOmitted: true);

        new { Participants = Participants(), Left = _left, Joined = _joined }.Should().BeEquivalentTo(
            new { Participants = new[] { ChannelA, ChannelB }.Order().ToArray(), Left = Array.Empty<string>(), Joined = Array.Empty<string>() },
            "the participant joined after the listing was requested, so the listing's silence about it is no evidence");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldNotBringBackAParticipantThatLeftAfterTheRequest_WhenTheListingStillReportsIt()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        _sut.OnUserJoined(Room, 0, ChannelB, "b");
        using var window = _sut.OpenSnapshotWindow();
        _sut.OnUserLeft(Room, ChannelB);
        _left.Clear();

        _sut.ReconcileWithSnapshot(window, [Room], Rows(Row(ChannelA), Row(ChannelB)), removeOmitted: true);

        new { Participants = Participants(), Left = _left }.Should().BeEquivalentTo(
            new { Participants = new[] { ChannelA }, Left = Array.Empty<string>() },
            "Asterisk answered before the leave the SDK saw after the request: the listing does not bring it back, and "
            + "the leave already raised its UserLeft");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldLeaveARoomAlone_WhenItsParticipantListingDidNotComplete()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        using var window = _sut.OpenSnapshotWindow();

        _sut.ReconcileWithSnapshot(window, [Room], new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>(),
            removeOmitted: true);

        new { Participants = Participants(), Left = _left }.Should().BeEquivalentTo(
            new { Participants = new[] { ChannelA }, Left = Array.Empty<string>() },
            "the room is listed but its ConfbridgeList was refused or cut short: that is no evidence anyone left");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldAdmitSilentlyButRemoveNoOne_WhenTheAnswersCompletionCannotBeTold()
    {
        _sut.OnUserJoined("other", 0, ChannelC, "c");
        using var window = _sut.OpenSnapshotWindow();
        _joined.Clear();

        _sut.ReconcileWithSnapshot(window, [Room], Rows(Row(ChannelA, muted: true)), removeOmitted: false);

        new
        {
            Participants = Participants(),
            Other = _sut.GetRoom("other")?.Users.Keys.ToArray(),
            Muted = _sut.GetRoom(Room)?.Users[ChannelA].Muted,
            UniqueId = _sut.GetRoom(Room)?.Users[ChannelA].UniqueId,
            Left = _left,
            Joined = _joined,
        }.Should().BeEquivalentTo(new
        {
            Participants = new[] { ChannelA },
            Other = new[] { ChannelC },
            Muted = (bool?)true,
            UniqueId = $"uid-{ChannelA}",
            Left = Array.Empty<string>(),
            Joined = Array.Empty<string>(),
        }, "only the admissions apply: the listed participant is admitted silently with its row's values, and the room "
            + "the listing omits keeps its participant");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepTheHeldEntryAndRefreshOnlyReadableValues_WhenTheListingReportsAHeldParticipant()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        _sut.OnUserJoined(Room, 0, ChannelB, "b");
        var heldA = _sut.GetRoom(Room)!.Users[ChannelA];
        var heldB = _sut.GetRoom(Room)!.Users[ChannelB];
        heldB.Muted = true;
        var changed = new List<string>();
        _sut.UserStateChanged += (_, u) => changed.Add(u.Channel);
        using var window = _sut.OpenSnapshotWindow();
        _joined.Clear();

        _sut.ReconcileWithSnapshot(window, [Room],
            Rows(Row(ChannelA, muted: true, talking: null), Row(ChannelB, muted: null, talking: null)), removeOmitted: true);

        new
        {
            SameA = ReferenceEquals(_sut.GetRoom(Room)!.Users[ChannelA], heldA),
            SameB = ReferenceEquals(_sut.GetRoom(Room)!.Users[ChannelB], heldB),
            heldA.Muted,
            BMuted = heldB.Muted,
            Changed = changed,
            Joined = _joined,
            Left = _left,
        }.Should().BeEquivalentTo(new
        {
            SameA = true,
            SameB = true,
            Muted = true,
            BMuted = true,
            Changed = new[] { ChannelA },
            Joined = Array.Empty<string>(),
            Left = Array.Empty<string>(),
        }, "a held participant is a replacement: it keeps its entry; a readable value that differs updates it and raises "
            + "UserStateChanged, and an unreadable one leaves it as it was");
    }
}
