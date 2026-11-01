using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Live.Tests.MeetMe;

/// <summary>
/// What a <see cref="MeetMeManager.UserLeft"/> handler reads from <see cref="MeetMeUser.LeaveNotObserved"/>, one test per
/// path that takes a participant out of the table. Each reads the value inside the handler, where a consumer does. This
/// assembly is a friend of Live, so these prove the marking; what a consumer can read and assign is proved by the
/// compilations in the DocSnippets tests.
/// </summary>
public sealed class ConferenceLeaveNotObservedTests
{
    private const string Room = "100";
    private const string ChannelA = "Local/conf@probe-00000000;2";
    private const string ChannelB = "Local/conf@probe-00000002;2";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>> NoRows =
        new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>();

    private readonly MeetMeManager _sut = new(NullLogger.Instance);
    private readonly List<ReadInHandler> _left = [];

    public ConferenceLeaveNotObservedTests() =>
        _sut.UserLeft += (room, user) => _left.Add(new ReadInHandler(room.RoomNumber, user.Channel, user.LeaveNotObserved));

    [Fact]
    public void ReconcileWithSnapshot_ShouldLetTheHandlerReadTrue_WhenAListedRoomOmitsTheParticipant()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        _sut.OnUserJoined(Room, 0, ChannelB, "b");
        using var window = _sut.OpenSnapshotWindow();
        var rows = new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>
        {
            [Room] = [new ConferenceSnapshotParticipant(ChannelA, "a", false, false)],
        };

        _sut.ReconcileWithSnapshot(window, [Room], rows, removeOmitted: true);

        _left.Should().Equal(new ReadInHandler(Room, ChannelB, LeaveNotObserved: true));
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldLetTheHandlerReadTrue_WhenTheRoomIsNotListed()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        using var window = _sut.OpenSnapshotWindow();

        _sut.ReconcileWithSnapshot(window, ["other"], NoRows, removeOmitted: true);

        _left.Should().Equal(new ReadInHandler(Room, ChannelA, LeaveNotObserved: true));
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldLetTheHandlerReadTrue_WhenAsteriskAnswersNoActiveConferences()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        using var window = _sut.OpenSnapshotWindow();

        _sut.ReconcileWithSnapshot(window, [], NoRows, removeOmitted: true);

        _left.Should().Equal(new ReadInHandler(Room, ChannelA, LeaveNotObserved: true));
    }

    [Fact]
    public void OnUserLeft_ShouldLetTheHandlerReadFalse_WhenAsteriskReportsTheLeave()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");

        _sut.OnUserLeft(Room, ChannelA);

        _left.Should().Equal(new ReadInHandler(Room, ChannelA, LeaveNotObserved: false));
    }

    [Fact]
    public void OnConferenceEnded_ShouldLetTheHandlerReadFalse_WhenAsteriskEndsTheConference()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");

        _sut.OnConferenceEnded(Room);

        _left.Should().Equal(new ReadInHandler(Room, ChannelA, LeaveNotObserved: false));
    }

    [Fact]
    public void Clear_ShouldMarkTheParticipantAConsumerHolds_WhenTheHostEmptiesTheTable()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var held = _sut.GetRoom(Room)!.Users[ChannelA];

        _sut.Clear();

        new { Raised = _left.Count, held.LeaveNotObserved, held.State }.Should().BeEquivalentTo(
            new { Raised = 0, LeaveNotObserved = true, State = MeetMeUserState.Left },
            "Clear raises nothing, so the mark is readable only on the instance the table held");
    }

    [Fact]
    public void OnUserJoined_ShouldMarkTheReplacedParticipant_WhenAnotherUniqueidTakesTheChannel()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var replaced = _sut.GetRoom(Room)!.Users[ChannelA];
        var readInHandler = new List<object>();
        _sut.UserJoined += (_, user) => readInHandler.Add(new
        {
            Replaced = new { replaced.LeaveNotObserved, replaced.State },
            Joined = new { user.UniqueId, user.LeaveNotObserved, user.State },
        });

        _sut.OnUserJoined(Room, 0, ChannelA, "b");

        new { Left = _left.Count, Joins = readInHandler }.Should().BeEquivalentTo(new
        {
            Left = 0,
            Joins = new object[]
            {
                new
                {
                    Replaced = new { LeaveNotObserved = true, State = MeetMeUserState.Left },
                    Joined = new { UniqueId = "b", LeaveNotObserved = false, State = MeetMeUserState.Joined },
                },
            },
        });
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData(null, "b")]
    [InlineData("a", null)]
    [InlineData("", "b")]
    [InlineData("a", "")]
    public void OnUserJoined_ShouldLeaveTheReplacedInstanceUnmarked_WhenTheJoinRepeatsTheParticipant(string? held, string? joining)
    {
        _sut.OnUserJoined(Room, 0, ChannelA, held);
        var repeated = _sut.GetRoom(Room)!.Users[ChannelA];

        _sut.OnUserJoined(Room, 0, ChannelA, joining);

        new { Left = _left.Count, repeated.LeaveNotObserved, repeated.State }.Should().BeEquivalentTo(
            new { Left = 0, LeaveNotObserved = false, State = MeetMeUserState.Joined },
            "the same Uniqueid, or a side without one, is the same participant, who did not leave");
    }

    [Fact]
    public void OnUserLeft_ShouldLeaveTheParticipantToReadTrue_WhenTheReportNamesAChannelLiveDoesNotHold()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");

        _sut.OnUserLeft(Room, ChannelB);
        using var window = _sut.OpenSnapshotWindow();
        _sut.ReconcileWithSnapshot(window, [], NoRows, removeOmitted: true);

        _left.Should().Equal(new ReadInHandler(Room, ChannelA, LeaveNotObserved: true));
    }

    [Fact]
    public void OnUserLeft_ShouldLetTheHandlerReadFalse_WhenALateReportNamesAReusedChannel()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        _sut.OnUserJoined(Room, 0, ChannelA, "b");

        // The earlier participant's late report: leaves are matched by channel name, so it removes whoever holds the name.
        _sut.OnUserLeft(Room, ChannelA);

        _left.Should().Equal(new ReadInHandler(Room, ChannelA, LeaveNotObserved: false));
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldHaveTheMarkFinal_WhenTheLeaveIsPosted()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, "a");
        var readWhenPosted = new List<bool>();
        _sut.Events.Posted = (_, user) => readWhenPosted.Add(((MeetMeUser)user!).LeaveNotObserved);
        using var window = _sut.OpenSnapshotWindow();

        _sut.ReconcileWithSnapshot(window, [], NoRows, removeOmitted: true);

        readWhenPosted.Should().Equal([true], "a mark written after the post could reach a handler on another thread late");
    }

    private sealed record ReadInHandler(string Room, string Channel, bool LeaveNotObserved);
}
