using Asterisk.Sdk.Live.MeetMe;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.MeetMe;

public sealed class MeetMeManagerTests
{
    private readonly MeetMeManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnUserJoined_ShouldCreateRoom_WhenRoomDoesNotExist()
    {
        _sut.OnUserJoined("100", 1, "PJSIP/2000-001");

        var room = _sut.GetRoom("100");
        room.Should().NotBeNull();
        room!.RoomNumber.Should().Be("100");
        room.UserCount.Should().Be(1);
        room.Users.Should().ContainKey(1);
        room.Users[1].Channel.Should().Be("PJSIP/2000-001");
        room.Users[1].State.Should().Be(MeetMeUserState.Joined);
    }

    [Fact]
    public void OnUserJoined_ShouldAddToExistingRoom_WhenRoomAlreadyExists()
    {
        _sut.OnUserJoined("100", 1, "PJSIP/2000-001");
        _sut.OnUserJoined("100", 2, "PJSIP/3000-001");

        var room = _sut.GetRoom("100");
        room.Should().NotBeNull();
        room!.UserCount.Should().Be(2);
        room.Users.Should().ContainKey(1);
        room.Users.Should().ContainKey(2);
    }

    [Fact]
    public void OnUserLeft_ShouldRemoveUser_WhenOtherUsersRemain()
    {
        _sut.OnUserJoined("100", 1, "PJSIP/2000-001");
        _sut.OnUserJoined("100", 2, "PJSIP/3000-001");

        _sut.OnUserLeft("100", 1);

        var room = _sut.GetRoom("100");
        room.Should().NotBeNull();
        room!.UserCount.Should().Be(1);
        room.Users.Should().NotContainKey(1);
        room.Users.Should().ContainKey(2);
    }

    [Fact]
    public void OnUserLeft_ShouldRemoveRoom_WhenLastUserLeaves()
    {
        _sut.OnUserJoined("100", 1, "PJSIP/2000-001");

        _sut.OnUserLeft("100", 1);

        _sut.GetRoom("100").Should().BeNull();
        _sut.Rooms.Should().BeEmpty();
    }

    [Fact]
    public void OnUserLeft_ShouldSetUserStateToLeft()
    {
        MeetMeUser? leftUser = null;
        _sut.UserLeft += (_, user) => leftUser = user;

        _sut.OnUserJoined("100", 1, "PJSIP/2000-001");
        _sut.OnUserLeft("100", 1);

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
        firedUser!.UserNum.Should().Be(5);
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
        _sut.OnUserLeft("200", 5);

        firedRoom.Should().NotBeNull();
        firedRoom!.RoomNumber.Should().Be("200");
        firedUser.Should().NotBeNull();
        firedUser!.UserNum.Should().Be(5);
    }

    [Fact]
    public void UserLeft_ShouldNotFireEvent_WhenRoomDoesNotExist()
    {
        var fired = false;
        _sut.UserLeft += (_, _) => fired = true;

        _sut.OnUserLeft("nonexistent", 1);

        fired.Should().BeFalse();
    }

    [Fact]
    public void GetRoom_ShouldReturnNull_WhenRoomDoesNotExist()
    {
        _sut.GetRoom("999").Should().BeNull();
    }

    [Fact]
    public void Rooms_ShouldReturnCorrectSnapshot()
    {
        _sut.OnUserJoined("100", 1, "PJSIP/2000-001");
        _sut.OnUserJoined("200", 1, "PJSIP/3000-001");

        _sut.Rooms.Should().HaveCount(2);
        _sut.Rooms.Select(r => r.RoomNumber).Should().BeEquivalentTo(["100", "200"]);
    }

    [Fact]
    public void Clear_ShouldResetEverything()
    {
        _sut.OnUserJoined("100", 1, "PJSIP/2000-001");
        _sut.OnUserJoined("200", 1, "PJSIP/3000-001");

        _sut.Clear();

        _sut.Rooms.Should().BeEmpty();
        _sut.GetRoom("100").Should().BeNull();
        _sut.GetRoom("200").Should().BeNull();
    }
}
