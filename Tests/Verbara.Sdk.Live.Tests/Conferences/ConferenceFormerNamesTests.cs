using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Conferences;
using Verbara.Sdk.Live.MeetMe;
using Verbara.Sdk.Live.Server;

// These tests name the former MeetMe symbols on purpose: they hold them to reaching the conference names' objects.
#pragma warning disable VSDK0008

namespace Verbara.Sdk.Live.Tests.Conferences;

/// <summary>
/// The former <c>MeetMe*</c> names and the conference names reach one table: one manager, the same room and participant
/// instances, and one subscription order for the handlers of both names.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class ConferenceFormerNamesTests : IAsyncLifetime
{
    private const string Room = "100";
    private const string ChannelA = "Local/conf@probe-00000000;2";
    private const string ChannelB = "Local/conf@probe-00000002;2";

    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly VerbaraServer _server;
    private IObserver<ManagerEvent>? _observer;

    public ConferenceFormerNamesTests()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs)).Returns(Substitute.For<IDisposable>());
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>()).Returns(Empty());
        _server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    public Task InitializeAsync() => _server.StartAsync();

    public Task DisposeAsync() => _server.DisposeAsync().AsTask().WaitAsync(CleanupBound);

    private static async IAsyncEnumerable<ManagerEvent> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private void Join(string channel) =>
        _observer!.OnNext(new ConfbridgeJoinEvent { Conference = Room, Channel = channel, UniqueId = channel + "-id" });

    private void Leave(string channel) =>
        _observer!.OnNext(new ConfbridgeLeaveEvent { Conference = Room, Channel = channel, UniqueId = channel + "-id" });

    [Fact]
    public void Conferences_ShouldBeTheMeetMeInstance_WhenReadOnOneServer()
    {
        _server.Conferences.Should().BeSameAs(_server.MeetMe);
        _server.Conferences.Should().BeOfType<MeetMeManager>();
    }

    [Fact]
    public void UserJoined_ShouldDeliverTheSameRoomAndParticipantToBothNames_WhenAParticipantJoins()
    {
        var byNew = new List<(ConferenceRoom Room, ConferenceUser User)>();
        var byOld = new List<(MeetMeRoom Room, MeetMeUser User)>();
        _server.Conferences.UserJoined += (r, u) => byNew.Add((r, u));
        _server.MeetMe.UserJoined += (r, u) => byOld.Add((r, u));

        Join(ChannelA);

        byNew.Should().ContainSingle();
        byOld.Should().ContainSingle();
        byNew[0].Room.Should().BeSameAs(byOld[0].Room);
        byNew[0].User.Should().BeSameAs(byOld[0].User);
        _server.Conferences.GetRoom(Room).Should().BeSameAs(byOld[0].Room);
        _server.MeetMe.GetRoom(Room).Should().BeSameAs(byOld[0].Room);
        _server.Conferences.Rooms.Should().ContainSingle().Which.Should().BeSameAs(byOld[0].Room);
        _server.MeetMe.Rooms.Should().ContainSingle().Which.Should().BeSameAs(byOld[0].Room);
        byNew[0].Room.Users[ChannelA].Should().BeSameAs(byOld[0].Room.Users[ChannelA]);
    }

    [Fact]
    public void UserLeft_ShouldRaiseBothNamesInOneSubscriptionOrder_WhenAnEarlierHandlerThrows()
    {
        var order = new List<string>();
        _server.MeetMe.UserLeft += (_, _) =>
        {
            order.Add("old-1");
            throw new InvalidOperationException("a failing subscriber");
        };
        _server.Conferences.UserLeft += (_, _) => order.Add("new");
        _server.MeetMe.UserLeft += (_, _) => order.Add("old-2");
        Join(ChannelA);

        Leave(ChannelA);

        order.Should().Equal("old-1", "new", "old-2");
    }

    [Fact]
    public void UserLeft_ShouldIsolateEachHandlerOfAMulticast_WhenItIsSubscribedUnderTheFormerName()
    {
        var order = new List<string>();
        Action<MeetMeRoom, MeetMeUser> first = (_, _) =>
        {
            order.Add("first");
            throw new InvalidOperationException("a failing subscriber");
        };
        Action<MeetMeRoom, MeetMeUser> second = (_, _) => order.Add("second");
        _server.MeetMe.UserLeft += first + second;
        Join(ChannelA);

        Leave(ChannelA);

        order.Should().Equal("first", "second");
    }

    [Fact]
    public void UserStateChanged_ShouldNotRaiseAFormerNameHandler_WhenItWasRemoved()
    {
        var raised = 0;
        var kept = 0;
        Action<MeetMeRoom, MeetMeUser> removed = (_, _) => raised++;
        _server.MeetMe.UserStateChanged += removed;
        _server.MeetMe.UserStateChanged += (_, _) => kept++;
        _server.MeetMe.UserStateChanged -= removed;
        Join(ChannelA);

        _observer!.OnNext(new ConfbridgeMuteEvent { Conference = Room, Channel = ChannelA });

        raised.Should().Be(0);
        kept.Should().Be(1);
    }

    [Fact]
    public void UserJoined_ShouldRemoveOnlyTheLastSubscription_WhenOneHandlerWasSubscribedTwice()
    {
        var raised = 0;
        Action<MeetMeRoom, MeetMeUser> handler = (_, _) => raised++;
        _server.MeetMe.UserJoined += handler;
        _server.MeetMe.UserJoined += handler;
        _server.MeetMe.UserJoined -= handler;

        Join(ChannelA);

        raised.Should().Be(1);
    }

    [Fact]
    public void Users_ShouldFollowTheTable_WhenAHeldViewOutlivesAJoinAndALeave()
    {
        Join(ChannelA);
        var view = _server.Conferences.GetRoom(Room)!.Users;

        Join(ChannelB);
        Leave(ChannelA);

        view.Keys.Should().Equal(ChannelB);
        view.Count.Should().Be(1);
        view.ContainsKey(ChannelA).Should().BeFalse();
        view.TryGetValue(ChannelB, out var user).Should().BeTrue();
        user.Should().BeSameAs(_server.MeetMe.GetRoom(Room)!.Users[ChannelB]);
        view.Values.Should().ContainSingle().Which.Should().BeSameAs(user);
        view.Should().ContainSingle().Which.Key.Should().Be(ChannelB);
        view.TryGetValue(ChannelA, out var missing).Should().BeFalse();
        missing.Should().BeNull();
    }

    [Fact]
    public void State_ShouldReadTheSameValueUnderBothEnums_WhenAParticipantLeaves()
    {
        Join(ChannelA);
        var user = _server.MeetMe.GetRoom(Room)!.Users[ChannelA];
        ConferenceUser asConference = user;
        asConference.State.Should().Be(ConferenceUserState.Joined);
        user.State.Should().Be(MeetMeUserState.Joined);

        Leave(ChannelA);

        asConference.State.Should().Be(ConferenceUserState.Left);
        user.State.Should().Be(MeetMeUserState.Left);
        ((int)ConferenceUserState.Joined).Should().Be((int)MeetMeUserState.Joined);
        ((int)ConferenceUserState.Left).Should().Be((int)MeetMeUserState.Left);
    }

    [Fact]
    public void FormerSetters_ShouldWriteWhatTheConferenceNamesRead_WhenAConsumerStillUsesThem()
    {
        Join(ChannelA);
        var user = _server.MeetMe.GetRoom(Room)!.Users[ChannelA];
        ConferenceUser asConference = user;

        user.Muted = true;
        user.Talking = true;

        asConference.Muted.Should().BeTrue();
        asConference.Talking.Should().BeTrue();
        asConference.Channel.Should().Be(user.Channel);
        asConference.UniqueId.Should().Be(user.UniqueId);
        asConference.UserNum.Should().Be(user.UserNum);
        asConference.LeaveNotObserved.Should().Be(user.LeaveNotObserved);
        _server.Conferences.GetRoom(Room)!.RoomNumber.Should().Be(_server.MeetMe.GetRoom(Room)!.RoomNumber);
        _server.Conferences.GetRoom(Room)!.UserCount.Should().Be(_server.MeetMe.GetRoom(Room)!.UserCount);
    }

    [Fact]
    public void FormerManagerMethods_ShouldChangeTheTableTheConferenceNamesRead_WhenCalledOnAStandaloneMeetMeManager()
    {
        var meetMe = new MeetMeManager(NullLogger.Instance);
        ConferenceManager conferences = meetMe;

        meetMe.OnUserJoined(Room, 1, ChannelA);
        meetMe.OnUserJoined(Room, 2, ChannelB, "b-id");
        conferences.GetRoom(Room)!.UserCount.Should().Be(2);

        meetMe.OnUserLeft(Room, ChannelA);
        conferences.GetRoom(Room)!.Users.Keys.Should().Equal(ChannelB);

        meetMe.OnConferenceEnded(Room);
        conferences.Rooms.Should().BeEmpty();

        meetMe.OnUserJoined(Room, 3, ChannelA);
        meetMe.Clear();
        conferences.Rooms.Should().BeEmpty();
        meetMe.Rooms.Should().BeEmpty();
    }
}
