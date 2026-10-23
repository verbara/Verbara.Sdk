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

    // --- Mute and talking state ----------------------------------------------------------------

    [Fact]
    public void OnUserJoined_ShouldSetMutedBeforeUserJoined_WhenJoinCarriesMuted()
    {
        bool? mutedSeenByUserJoined = null;
        var stateChanged = 0;
        _sut.UserJoined += (_, user) => mutedSeenByUserJoined = user.Muted;
        _sut.UserStateChanged += (_, _) => stateChanged++;

        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA, muted: true);

        _sut.GetRoom(Room)!.Users[ChannelA].Muted.Should().BeTrue();
        _sut.GetRoom(Room)!.Users[ChannelA].Talking.Should().BeFalse("a participant starts not talking");
        mutedSeenByUserJoined.Should().BeTrue("the muted value is in place before UserJoined is raised");
        stateChanged.Should().Be(0, "a join raises UserJoined only");
    }

    [Fact]
    public void OnUserJoined_ShouldReadNotMuted_WhenRecordedThroughPublicOverloads()
    {
        _sut.OnUserJoined(Room, 0, ChannelA);
        _sut.OnUserJoined(Room, 0, ChannelB, UniqueIdB);

        _sut.GetRoom(Room)!.Users[ChannelA].Muted.Should().BeFalse();
        _sut.GetRoom(Room)!.Users[ChannelB].Muted.Should().BeFalse();
    }

    [Fact]
    public void OnUserJoined_ShouldReseedMutedAndClearTalking_WhenHeldChannelJoinsAgain()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA, muted: true);
        _sut.OnUserTalking(Room, ChannelA, true);

        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA, muted: false);

        var user = _sut.GetRoom(Room)!.Users[ChannelA];
        user.Muted.Should().BeFalse("the new join carried not muted");
        user.Talking.Should().BeFalse("the new join starts not talking");
    }

    [Fact]
    public void OnUserMuted_ShouldMuteNamedParticipantAndRaiseOnce_WhenMuteThenRepeatThenUnmute()
    {
        JoinBoth();
        var notified = new List<(MeetMeRoom Room, MeetMeUser User, bool Muted)>();
        _sut.UserStateChanged += (room, user) => notified.Add((room, user, user.Muted));

        _sut.OnUserMuted(Room, ChannelA, true);

        _sut.GetRoom(Room)!.Users[ChannelA].Muted.Should().BeTrue();
        _sut.GetRoom(Room)!.Users[ChannelB].Muted.Should().BeFalse("the mute named the other participant");
        notified.Should().ContainSingle();
        notified[0].Room.RoomNumber.Should().Be(Room);
        notified[0].User.Channel.Should().Be(ChannelA);
        notified[0].Muted.Should().BeTrue("the notification is raised after the write");

        _sut.OnUserMuted(Room, ChannelA, true);
        notified.Should().ContainSingle("a mute of a muted participant changes nothing");

        _sut.OnUserMuted(Room, ChannelA, false);
        _sut.GetRoom(Room)!.Users[ChannelA].Muted.Should().BeFalse();
        notified.Should().HaveCount(2, "the unmute changed the state");
        notified[1].Muted.Should().BeFalse();
    }

    [Fact]
    public void OnUserMuted_ShouldRaiseNothing_WhenUnmutingParticipantNotMuted()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);
        var notified = 0;
        _sut.UserStateChanged += (_, _) => notified++;

        _sut.OnUserMuted(Room, ChannelA, false);

        notified.Should().Be(0);
        _sut.GetRoom(Room)!.Users[ChannelA].Muted.Should().BeFalse();
    }

    [Fact]
    public void OnUserTalking_ShouldSetTalkingAndRaiseOncePerChange_WhenOnRepeatThenOff()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);
        var notified = new List<bool>();
        _sut.UserStateChanged += (_, user) => notified.Add(user.Talking);

        _sut.OnUserTalking(Room, ChannelA, true);
        _sut.GetRoom(Room)!.Users[ChannelA].Talking.Should().BeTrue();

        _sut.OnUserTalking(Room, ChannelA, true);
        _sut.OnUserTalking(Room, ChannelA, false);

        _sut.GetRoom(Room)!.Users[ChannelA].Talking.Should().BeFalse();
        notified.Should().Equal([true, false], "on and off are changes, the repeated on is not");
    }

    [Fact]
    public void OnUserMutedAndOnUserTalking_ShouldLeaveEachOtherUnchanged()
    {
        _sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);

        _sut.OnUserTalking(Room, ChannelA, true);
        _sut.OnUserMuted(Room, ChannelA, true);

        var user = _sut.GetRoom(Room)!.Users[ChannelA];
        user.Talking.Should().BeTrue("a mute does not change the talking state");
        user.Muted.Should().BeTrue();

        _sut.OnUserTalking(Room, ChannelA, false);
        user.Muted.Should().BeTrue("a talking event does not change the mute state");
    }

    [Fact]
    public void OnUserMutedAndOnUserTalking_ShouldLogAppliedEvents()
    {
        var logger = new CapturingLogger();
        var sut = new MeetMeManager(logger);
        sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);

        sut.OnUserMuted(Room, ChannelA, true);
        sut.OnUserTalking(Room, ChannelA, true);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.StartsWith("[CONFERENCE] User muted", StringComparison.Ordinal) && e.Message.Contains("muted=True"));
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.StartsWith("[CONFERENCE] User talking", StringComparison.Ordinal) && e.Message.Contains("talking=True"));
    }

    [Theory]
    [InlineData("300", ChannelA, "mute")]
    [InlineData(Room, ChannelB, "mute")]
    [InlineData("300", ChannelA, "talking")]
    [InlineData(Room, ChannelB, "talking")]
    public void OnUserMutedOrOnUserTalking_ShouldCreateNothingOrNotify_WhenRoomOrParticipantIsNotHeldWithoutAnOpenRead(string roomNumber, string channel, string eventName)
    {
        var logger = new CapturingLogger();
        var sut = new MeetMeManager(logger);
        sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);
        var notified = 0;
        sut.UserStateChanged += (_, _) => notified++;

        if (eventName == "mute")
        {
            sut.OnUserMuted(roomNumber, channel, true);
        }
        else
        {
            sut.OnUserTalking(roomNumber, channel, true);
        }

        sut.Rooms.Select(r => r.RoomNumber).Should().BeEquivalentTo([Room], "a state event never creates a room");
        sut.GetRoom(Room)!.Users.Keys.Should().BeEquivalentTo([ChannelA], "a state event never creates a participant");
        sut.GetRoom(Room)!.Users[ChannelA].Muted.Should().BeFalse();
        sut.GetRoom(Room)!.Users[ChannelA].Talking.Should().BeFalse();
        notified.Should().Be(0);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("participant not held") && e.Message.Contains($"event={eventName}"));
    }

    [Theory]
    [InlineData("", ChannelA, "mute")]
    [InlineData(Room, "", "mute")]
    [InlineData("", ChannelA, "talking")]
    [InlineData(Room, "", "talking")]
    public void OnUserMutedOrOnUserTalking_ShouldNotApply_WhenConferenceOrChannelIsEmpty(string roomNumber, string channel, string eventName)
    {
        var logger = new CapturingLogger();
        var sut = new MeetMeManager(logger);
        sut.OnUserJoined(Room, 0, ChannelA, UniqueIdA);
        var notified = 0;
        sut.UserStateChanged += (_, _) => notified++;

        if (eventName == "mute")
        {
            sut.OnUserMuted(roomNumber, channel, true);
        }
        else
        {
            sut.OnUserTalking(roomNumber, channel, true);
        }

        sut.GetRoom(Room)!.Users[ChannelA].Muted.Should().BeFalse();
        sut.GetRoom(Room)!.Users[ChannelA].Talking.Should().BeFalse();
        notified.Should().Be(0);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("missing conference or channel") && e.Message.Contains($"event={eventName}"));
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
