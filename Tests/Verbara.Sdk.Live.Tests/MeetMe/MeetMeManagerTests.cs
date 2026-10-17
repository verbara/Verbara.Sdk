using Verbara.Sdk.Live.MeetMe;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Live.Tests.MeetMe;

/// <summary>
/// The conference table: one participant per channel. ConfBridge sends no user number, so every
/// ConfBridge participant is recorded with user number <c>0</c>; the channel is the key. The
/// channel and unique-id values are ones Asterisk 22 sent for Local channels in conference <c>100</c>.
/// </summary>
public sealed class MeetMeManagerTests
{
    private const string Room = "100";
    private const string ChannelA = "Local/conf@probe-00000000;2";
    private const string UniqueIdA = "1792103773.1";
    private const string ChannelB = "Local/conf@probe-00000002;2";
    private const string UniqueIdB = "1792103775.5";

    private readonly MeetMeManager _sut = new(NullLogger.Instance);

    private void JoinBoth()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);
        _sut.OnUserJoined(Room, 0, ChannelB, UniqueIdB);
    }

    [Fact]
    public void OnUserJoined_ShouldCreateRoom_WhenRoomDoesNotExist()
    {
        _sut.OnUserJoined(Room, 0, ChannelA);

        var room = _sut.GetRoom(Room);
        room.Should().NotBeNull("a join with no conference start before it creates the room");
        room!.RoomNumber.Should().Be(Room);
        room.UserCount.Should().Be(1);
        room.Users.Keys.Should().BeEquivalentTo([ChannelA]);
        room.Users[ChannelA].Channel.Should().Be(ChannelA);
        room.Users[ChannelA].State.Should().Be(MeetMeUserState.Joined);
    }

    [Fact]
    public void OnUserJoined_ShouldAddToExistingRoom_WhenRoomAlreadyExists()
    {
        var joined = new List<MeetMeUser>();
        _sut.UserJoined += (_, user) => joined.Add(user);

        JoinBoth();

        var room = _sut.GetRoom(Room);
        room.Should().NotBeNull();
        room!.UserCount.Should().Be(2, "two channels sharing user number 0 are two participants");
        room.Users.Keys.Should().BeEquivalentTo([ChannelA, ChannelB]);
        room.Users[ChannelA].Channel.Should().Be(ChannelA);
        room.Users[ChannelB].Channel.Should().Be(ChannelB);
        joined.Should().HaveCount(2, "each applied join raises UserJoined once");
    }

    [Fact]
    public void OnUserJoined_ShouldKeepUserNumberZeroForBoth_WhenTwoConfBridgeChannelsJoin()
    {
        JoinBoth();

        var room = _sut.GetRoom(Room)!;
        room.Users.Values.Select(u => u.UserNum).Should().Equal([0, 0], "ConfBridge sends no user number");
        room.UserCount.Should().Be(2, "the shared user number does not collapse the participants");
    }

    [Fact]
    public void OnUserJoined_ShouldStoreUniqueId_WhenJoinCarriesOne()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);

        _sut.GetRoom(Room)!.Users[ChannelA].UniqueId.Should().Be(UniqueIdA);
    }

    [Fact]
    public void OnUserJoined_ShouldLeaveUniqueIdNull_WhenRecordedWithoutOne()
    {
        _sut.OnUserJoined(Room, 0, ChannelA);

        _sut.GetRoom(Room)!.Users[ChannelA].UniqueId.Should().BeNull();
    }

    [Fact]
    public void OnUserJoined_ShouldCountTwoParticipants_WhenTwoChannelsCarryTheSameUniqueId()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);
        _sut.OnUserJoined(Room, 0, ChannelB, UniqueIdA);

        var room = _sut.GetRoom(Room)!;
        room.UserCount.Should().Be(2, "the unique id is data only and never identifies a participant");
        room.Users.Keys.Should().BeEquivalentTo([ChannelA, ChannelB]);
    }

    [Fact]
    public void OnUserJoined_ShouldKeepOneParticipant_WhenHeldChannelJoinsAgain()
    {
        var joined = new List<MeetMeUser>();
        _sut.UserJoined += (_, user) => joined.Add(user);

        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);

        var room = _sut.GetRoom(Room)!;
        room.UserCount.Should().Be(1);
        room.Users.Keys.Should().BeEquivalentTo([ChannelA]);
        joined.Should().HaveCount(2, "the repeated join is applied and raises UserJoined");
    }

    [Fact]
    public void OnUserLeft_ShouldRemoveUser_WhenOtherUsersRemain()
    {
        var left = new List<MeetMeUser>();
        _sut.UserLeft += (_, user) => left.Add(user);
        JoinBoth();

        _sut.OnUserLeft(Room, ChannelA);

        var room = _sut.GetRoom(Room);
        room.Should().NotBeNull("one participant is still in the conference");
        room!.UserCount.Should().Be(1);
        room.Users.Keys.Should().BeEquivalentTo([ChannelB]);
        left.Should().ContainSingle().Which.Channel.Should().Be(ChannelA);
        left[0].State.Should().Be(MeetMeUserState.Left);
    }

    [Fact]
    public void OnUserLeft_ShouldRemoveRoom_WhenLastUserLeaves()
    {
        JoinBoth();
        _sut.OnUserLeft(Room, ChannelA);
        _sut.GetRoom(Room)!.UserCount.Should().Be(1, "the first leave keeps the other participant");

        _sut.OnUserLeft(Room, ChannelB);

        _sut.GetRoom(Room).Should().BeNull();
        _sut.Rooms.Should().BeEmpty();
    }

    [Fact]
    public void OnUserLeft_ShouldChangeNothing_WhenChannelIsNotHeld()
    {
        var fired = false;
        _sut.UserLeft += (_, _) => fired = true;
        JoinBoth();

        _sut.OnUserLeft(Room, "Local/conf@probe-00000003;2");

        _sut.GetRoom(Room)!.Users.Keys.Should().BeEquivalentTo([ChannelA, ChannelB]);
        fired.Should().BeFalse("no held participant was named");
    }

    [Fact]
    public void OnUserLeft_ShouldSetUserStateToLeft()
    {
        MeetMeUser? leftUser = null;
        _sut.UserLeft += (_, user) => leftUser = user;

        _sut.OnUserJoined(Room, 0, ChannelA);
        _sut.OnUserLeft(Room, ChannelA);

        leftUser.Should().NotBeNull();
        leftUser!.State.Should().Be(MeetMeUserState.Left);
    }

    [Fact]
    public void UserJoined_ShouldFireEvent_WhenUserJoins()
    {
        MeetMeRoom? firedRoom = null;
        MeetMeUser? firedUser = null;
        _sut.UserJoined += (room, user) =>
        {
            firedRoom = room;
            firedUser = user;
        };

        _sut.OnUserJoined("200", 5, "PJSIP/4000-001");

        firedRoom.Should().NotBeNull();
        firedRoom!.RoomNumber.Should().Be("200");
        firedUser.Should().NotBeNull();
        firedUser!.UserNum.Should().Be(5, "the user number is stored exactly as sent");
        firedUser.Channel.Should().Be("PJSIP/4000-001");
    }

    [Fact]
    public void UserLeft_ShouldFireEvent_WhenUserLeaves()
    {
        MeetMeRoom? firedRoom = null;
        MeetMeUser? firedUser = null;
        _sut.UserLeft += (room, user) =>
        {
            firedRoom = room;
            firedUser = user;
        };

        _sut.OnUserJoined("200", 5, "PJSIP/4000-001");
        _sut.OnUserLeft("200", "PJSIP/4000-001");

        firedRoom.Should().NotBeNull();
        firedRoom!.RoomNumber.Should().Be("200");
        firedUser.Should().NotBeNull();
        firedUser!.UserNum.Should().Be(5);
        firedUser.Channel.Should().Be("PJSIP/4000-001");
    }

    [Fact]
    public void UserLeft_ShouldNotFireEvent_WhenRoomDoesNotExist()
    {
        var fired = false;
        _sut.UserLeft += (_, _) => fired = true;

        _sut.OnUserLeft("nonexistent", ChannelA);

        fired.Should().BeFalse();
    }

    [Fact]
    public void OnConferenceEnded_ShouldRemoveRemainingParticipantsAndRoom_WhenRoomIsHeld()
    {
        var left = new List<(MeetMeUser User, MeetMeUserState StateWhenRaised)>();
        _sut.UserLeft += (_, user) => left.Add((user, user.State));
        JoinBoth();

        _sut.OnConferenceEnded(Room);

        _sut.GetRoom(Room).Should().BeNull("the conference ended");
        left.Select(l => l.User.Channel).Should().BeEquivalentTo([ChannelA, ChannelB],
            "UserLeft is raised once for each participant still held");
        left.Select(l => l.StateWhenRaised).Should().AllBeEquivalentTo(MeetMeUserState.Left);
    }

    [Fact]
    public void OnConferenceEnded_ShouldChangeNothing_WhenRoomIsNotHeld()
    {
        var fired = false;
        _sut.UserLeft += (_, _) => fired = true;
        JoinBoth();

        _sut.OnConferenceEnded("200");

        _sut.GetRoom(Room)!.Users.Keys.Should().BeEquivalentTo([ChannelA, ChannelB]);
        _sut.Rooms.Should().ContainSingle();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnConferenceEnded_ShouldLetLaterJoinRecreateRoom_WhenReplayingLeaveLeaveEndJoin()
    {
        // Asterisk 22.10.1, two Local participants in conference 100: both join, "kick all" makes
        // both leave, the conference ends, and one channel re-enters ConfBridge(100) through its next
        // dialplan priority; its join arrives with no conference start before it.
        const string first = "Local/conf@probe-00000000;2";
        const string firstUid = "1792104515.1";
        const string second = "Local/conf@probe-00000003;2";
        const string secondUid = "1792104517.7";
        var left = new List<MeetMeUser>();
        _sut.UserLeft += (_, user) => left.Add(user);
        _sut.OnUserJoined(Room, 0, first, firstUid);
        _sut.OnUserJoined(Room, 0, second, secondUid);

        _sut.OnUserLeft(Room, second);
        _sut.OnUserLeft(Room, first);
        var leftBeforeEnd = left.Count;
        _sut.OnConferenceEnded(Room);
        _sut.OnUserJoined(Room, 0, second, secondUid);

        var room = _sut.GetRoom(Room);
        room.Should().NotBeNull("a join after the end creates the room anew");
        room!.Users.Keys.Should().BeEquivalentTo([second]);
        room.Users[second].UniqueId.Should().Be(secondUid);
        leftBeforeEnd.Should().Be(2, "each leave raised UserLeft once");
        left.Should().HaveCount(leftBeforeEnd, "the end found no participant left to remove");
    }

    [Theory]
    [InlineData("", ChannelA)]
    [InlineData(Room, "")]
    public void OnUserJoined_ShouldNotApply_WhenConferenceOrChannelIsEmpty(string roomNumber, string channel)
    {
        var logger = new CapturingLogger();
        var sut = new MeetMeManager(logger);
        var fired = false;
        sut.UserJoined += (_, _) => fired = true;

        sut.OnUserJoined(roomNumber, 0, channel, UniqueIdA);

        sut.Rooms.Should().BeEmpty("a join without a conference or channel is not counted");
        fired.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("event=join"));
    }

    [Theory]
    [InlineData("", ChannelA)]
    [InlineData(Room, "")]
    public void OnUserLeft_ShouldNotApply_WhenConferenceOrChannelIsEmpty(string roomNumber, string channel)
    {
        var logger = new CapturingLogger();
        var sut = new MeetMeManager(logger);
        var fired = false;
        sut.UserLeft += (_, _) => fired = true;
        sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);

        sut.OnUserLeft(roomNumber, channel);

        sut.GetRoom(Room)!.Users.Keys.Should().BeEquivalentTo([ChannelA], "a leave without a channel removes nothing");
        fired.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("event=leave"));
    }

    [Fact]
    public void OnConferenceEnded_ShouldNotApply_WhenConferenceIsEmpty()
    {
        var logger = new CapturingLogger();
        var sut = new MeetMeManager(logger);
        var fired = false;
        sut.UserLeft += (_, _) => fired = true;
        sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);

        sut.OnConferenceEnded("");

        sut.GetRoom(Room)!.Users.Keys.Should().BeEquivalentTo([ChannelA]);
        fired.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("event=end"));
    }

    [Fact]
    public void GetRoom_ShouldReturnNull_WhenRoomDoesNotExist()
    {
        _sut.GetRoom("999").Should().BeNull();
    }

    [Fact]
    public void Rooms_ShouldReturnCorrectSnapshot()
    {
        _sut.OnUserJoined("100", 0, "PJSIP/2000-001");
        _sut.OnUserJoined("200", 0, "PJSIP/3000-001");

        _sut.Rooms.Should().HaveCount(2);
        _sut.Rooms.Select(r => r.RoomNumber).Should().BeEquivalentTo(["100", "200"]);
    }

    [Fact]
    public void Clear_ShouldResetEverything()
    {
        _sut.OnUserJoined("100", 0, "PJSIP/2000-001");
        _sut.OnUserJoined("200", 0, "PJSIP/3000-001");

        _sut.Clear();

        _sut.Rooms.Should().BeEmpty();
        _sut.GetRoom("100").Should().BeNull();
        _sut.GetRoom("200").Should().BeNull();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
