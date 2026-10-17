using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.MeetMe;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// Routes ConfBridge join and leave events through the live server's observer with two
/// participants in one conference. The channel and unique-id values are the ones Asterisk 22
/// sent for two Local channels joining conference <c>100</c>.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerbaraServerConferenceTests : IAsyncLifetime
{
    private const string Conference = "100";
    private const string FirstChannel = "Local/conf@probe-00000000;2";
    private const string FirstUniqueId = "1792103773.1";
    private const string SecondChannel = "Local/conf@probe-00000002;2";
    private const string SecondUniqueId = "1792103775.5";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection;
    private readonly VerbaraServer _sut;
    private IObserver<ManagerEvent>? _observer;

    public VerbaraServerConferenceTests()
    {
        _connection = Substitute.For<IAmiConnection>();

        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
            .Returns(Substitute.For<IDisposable>());

        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable());

        _sut = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _sut.DisposeAsync().AsTask().WaitAsync(CleanupBound);

    private static async IAsyncEnumerable<ManagerEvent> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async Task StartAndGetObserverAsync()
    {
        await _sut.StartAsync();
        _observer.Should().NotBeNull("StartAsync must capture the observer");
    }

    private void JoinBoth()
    {
        _observer!.OnNext(new ConfbridgeJoinEvent { Conference = Conference, Channel = FirstChannel, UniqueId = FirstUniqueId });
        _observer!.OnNext(new ConfbridgeJoinEvent { Conference = Conference, Channel = SecondChannel, UniqueId = SecondUniqueId });
    }

    private void Leave(string channel, string uniqueId) =>
        _observer!.OnNext(new ConfbridgeLeaveEvent { Conference = Conference, Channel = channel, UniqueId = uniqueId });

    [Fact]
    public async Task ConfbridgeJoin_ShouldCountBothParticipants_WhenTwoChannelsJoinOneConference()
    {
        await StartAndGetObserverAsync();

        JoinBoth();

        var room = _sut.MeetMe.GetRoom(Conference);
        room.Should().NotBeNull("two joins open the conference");
        room!.UserCount.Should().Be(2, "two distinct channels joined the conference");
        room.Users.Values.Select(u => u.Channel).Should().BeEquivalentTo(
            [FirstChannel, SecondChannel], "each joined channel is held as its own participant");
    }

    [Fact]
    public async Task ConfbridgeLeave_ShouldKeepRoomWithOtherParticipant_WhenOneOfTwoLeaves()
    {
        await StartAndGetObserverAsync();
        JoinBoth();

        Leave(FirstChannel, FirstUniqueId);

        (_sut.MeetMe.GetRoom(Conference)?.UserCount).Should().Be(1, "one participant is still in the conference");
        (_sut.MeetMe.GetRoom(Conference)?.Users.Values.Select(u => u.Channel)).Should().BeEquivalentTo(
            [SecondChannel], "only the channel that left is removed");
    }

    [Fact]
    public async Task ConfbridgeLeave_ShouldRemoveRoom_WhenSecondOfTwoLeaves()
    {
        await StartAndGetObserverAsync();
        JoinBoth();
        Leave(FirstChannel, FirstUniqueId);
        (_sut.MeetMe.GetRoom(Conference)?.UserCount).Should().Be(1, "the first leave keeps the other participant");

        Leave(SecondChannel, SecondUniqueId);

        _sut.MeetMe.GetRoom(Conference).Should().BeNull("the last participant left the conference");
    }

    [Fact]
    public async Task ConfbridgeEvents_ShouldRaiseUserJoinedAndUserLeftTwice_WhenTwoParticipantsJoinAndLeave()
    {
        var joined = new List<MeetMeUser>();
        var left = new List<MeetMeUser>();
        _sut.MeetMe.UserJoined += (_, user) => joined.Add(user);
        _sut.MeetMe.UserLeft += (_, user) => left.Add(user);
        await StartAndGetObserverAsync();

        JoinBoth();
        Leave(FirstChannel, FirstUniqueId);
        Leave(SecondChannel, SecondUniqueId);

        joined.Should().HaveCount(2, "each join raises UserJoined once");
        left.Should().HaveCount(2, "each leave raises UserLeft once");
    }
}
