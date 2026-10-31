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

    private sealed record ReadInHandler(string Room, string Channel, bool LeaveNotObserved);
}
