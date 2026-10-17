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
/// Routes ConfBridge join, mute, unmute and talking events through the live server's observer and
/// reads each participant's <see cref="MeetMeUser.Muted"/> and <see cref="MeetMeUser.Talking"/>.
/// The conference, channel and muted values are the ones Asterisk 20, 22 and 23 sent for three
/// Local channels in conference <c>100</c>: one joined with a start-muted user profile
/// (<c>Muted: Yes</c>), two with the default profile (<c>Muted: No</c>).
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerbaraServerConferenceStateTests : IAsyncLifetime
{
    private const string Conference = "100";
    private const string AdminChannel = "Local/admin@probe-00000000;2";
    private const string MutedChannel = "Local/muted@probe-00000002;2";
    private const string TalkerChannel = "Local/conf@probe-00000003;2";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection;
    private readonly VerbaraServer _sut;
    private IObserver<ManagerEvent>? _observer;

    public VerbaraServerConferenceStateTests()
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

    private void Join(string channel, string? muted, string conference = Conference) =>
        _observer!.OnNext(new ConfbridgeJoinEvent { Conference = conference, Channel = channel, Muted = muted });

    private void Mute(string? channel, string conference = Conference) =>
        _observer!.OnNext(new ConfbridgeMuteEvent { Conference = conference, Channel = channel });

    private void Unmute(string? channel, string conference = Conference) =>
        _observer!.OnNext(new ConfbridgeUnmuteEvent { Conference = conference, Channel = channel });

    private void Talking(string channel, bool? status, string conference = Conference) =>
        _observer!.OnNext(new ConfbridgeTalkingEvent { Conference = conference, Channel = channel, TalkingStatus = status });

    private MeetMeUser Participant(string channel)
    {
        var room = _sut.MeetMe.GetRoom(Conference);
        room.Should().NotBeNull("the join opened conference {0}", Conference);
        room!.Users.Should().ContainKey(channel, "the participant is keyed by its join's channel");
        return room.Users[channel];
    }

    // --- Mute state ---------------------------------------------------------------------------

    [Fact]
    public async Task ConfbridgeJoin_ShouldReadMuted_WhenJoinCarriesMutedYes()
    {
        bool? mutedSeenByUserJoined = null;
        _sut.MeetMe.UserJoined += (_, user) => mutedSeenByUserJoined = user.Muted;
        await StartAndGetObserverAsync();

        Join(MutedChannel, "Yes");

        Participant(MutedChannel).Muted.Should().BeTrue("the join carried Muted: Yes, sent for a start-muted user profile");
        mutedSeenByUserJoined.Should().BeTrue("the muted value is in place before UserJoined is raised");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("maybe")]
    [InlineData("No")]
    public async Task ConfbridgeJoin_ShouldReadNotMuted_WhenJoinCarriesNoOrNoReadableMutedValue(string? muted)
    {
        await StartAndGetObserverAsync();

        Join(AdminChannel, muted);

        Participant(AdminChannel).Muted.Should().BeFalse("an absent, unreadable or No muted value reads not muted");
    }

    [Fact]
    public async Task ConfbridgeMute_ShouldMuteOnlyNamedParticipant_AndUnmuteShouldClearIt()
    {
        await StartAndGetObserverAsync();
        Join(AdminChannel, "No");
        Join(TalkerChannel, "No");

        Mute(AdminChannel);

        Participant(AdminChannel).Muted.Should().BeTrue("ConfbridgeMute names this participant");
        Participant(TalkerChannel).Muted.Should().BeFalse("the mute named the other participant");

        Unmute(AdminChannel);

        Participant(AdminChannel).Muted.Should().BeFalse("ConfbridgeUnmute names this participant");
    }

    [Fact]
    public async Task ConfbridgeStateEvents_ShouldRaiseNoUserJoinedOrUserLeft_WhenParticipantIsMutedUnmutedAndTalks()
    {
        await StartAndGetObserverAsync();
        Join(AdminChannel, "No");
        var joined = 0;
        var left = 0;
        _sut.MeetMe.UserJoined += (_, _) => joined++;
        _sut.MeetMe.UserLeft += (_, _) => left++;

        Mute(AdminChannel);
        Unmute(AdminChannel);
        Talking(AdminChannel, true);

        joined.Should().Be(0, "a mute, an unmute or a talking event is not a join");
        left.Should().Be(0, "a mute, an unmute or a talking event is not a leave");
    }

    // --- Talking state ------------------------------------------------------------------------

    [Fact]
    public async Task ConfbridgeTalking_ShouldReadTalking_WhenTalkingStatusIsOn()
    {
        await StartAndGetObserverAsync();
        Join(TalkerChannel, "No");

        Talking(TalkerChannel, true);

        Participant(TalkerChannel).Talking.Should().BeTrue("ConfbridgeTalking carried TalkingStatus: on");
    }

    [Fact]
    public async Task ConfbridgeTalking_ShouldReadNotTalking_WhenTalkingStatusOnIsFollowedByOff()
    {
        await StartAndGetObserverAsync();
        Join(TalkerChannel, "No");

        Talking(TalkerChannel, true);
        Participant(TalkerChannel).Talking.Should().BeTrue("ConfbridgeTalking carried TalkingStatus: on");

        Talking(TalkerChannel, false);
        Participant(TalkerChannel).Talking.Should().BeFalse("ConfbridgeTalking then carried TalkingStatus: off");
    }

    [Fact]
    public async Task ConfbridgeTalking_ShouldKeepTalking_WhenTalkingStatusIsAbsentAfterOn()
    {
        await StartAndGetObserverAsync();
        Join(TalkerChannel, "No");

        Talking(TalkerChannel, true);
        Talking(TalkerChannel, null);

        Participant(TalkerChannel).Talking.Should().BeTrue("a talking event with no readable status leaves the state as it was");
    }

    [Fact]
    public async Task ConfbridgeTalking_ShouldReadNotTalking_WhenOnlyTalkingStatusOffArrives()
    {
        await StartAndGetObserverAsync();
        Join(TalkerChannel, "No");

        Talking(TalkerChannel, false);

        Participant(TalkerChannel).Talking.Should().BeFalse("ConfbridgeTalking carried TalkingStatus: off");
    }

    [Fact]
    public async Task ConfbridgeMuteAndTalking_ShouldLeaveEachOtherUnchanged_WhenTalkerIsMutedThenUnmuted()
    {
        await StartAndGetObserverAsync();
        Join(TalkerChannel, "No");

        Talking(TalkerChannel, true);
        Mute(TalkerChannel);

        Participant(TalkerChannel).Talking.Should().BeTrue("a mute does not change the talking state");
        Participant(TalkerChannel).Muted.Should().BeTrue("ConfbridgeMute names this participant");

        Unmute(TalkerChannel);

        Participant(TalkerChannel).Talking.Should().BeTrue("an unmute does not change the talking state");
        Participant(TalkerChannel).Muted.Should().BeFalse("ConfbridgeUnmute names this participant");
    }

    [Fact]
    public async Task ConfbridgeJoin_ShouldReseedMutedAndClearTalking_WhenHeldChannelJoinsAgainWithoutLeave()
    {
        await StartAndGetObserverAsync();
        Join(MutedChannel, "Yes");
        Talking(MutedChannel, true);
        Participant(MutedChannel).Muted.Should().BeTrue("the first join carried Muted: Yes");
        Participant(MutedChannel).Talking.Should().BeTrue("ConfbridgeTalking carried TalkingStatus: on");

        Join(MutedChannel, "No");

        _sut.MeetMe.GetRoom(Conference)!.UserCount.Should().Be(1, "a repeated join for a held channel replaces that participant");
        Participant(MutedChannel).Muted.Should().BeFalse("the new join carried Muted: No");
        Participant(MutedChannel).Talking.Should().BeFalse("a new join starts not talking");
    }

    // --- Events for participants the server does not hold -------------------------------------

    [Fact]
    public async Task ConfbridgeMute_ShouldCreateNothingAndLetJoinValueStand_WhenMuteArrivesBeforeItsJoin()
    {
        await StartAndGetObserverAsync();

        Mute(AdminChannel);

        _sut.MeetMe.Rooms.Should().BeEmpty("a mute for a participant not held creates no room");

        Join(AdminChannel, "No");

        Participant(AdminChannel).Muted.Should().BeFalse("the join carried Muted: No and the earlier mute was dropped");
    }

    [Fact]
    public async Task ConfbridgeMuteAndTalking_ShouldCreateNothing_WhenConferenceWasNeverJoined()
    {
        await StartAndGetObserverAsync();

        Mute(AdminChannel, conference: "300");
        Talking(AdminChannel, true, conference: "300");

        _sut.MeetMe.Rooms.Should().BeEmpty("a mute or talking event for a conference not held creates no room");
    }

    [Fact]
    public async Task ConfbridgeMute_ShouldChangeNoOne_WhenItNamesAnotherChannelOfAHeldRoom()
    {
        await StartAndGetObserverAsync();
        Join(AdminChannel, "No");

        Mute(TalkerChannel);

        _sut.MeetMe.GetRoom(Conference)!.UserCount.Should().Be(1, "a mute for a channel not held creates no participant");
        Participant(AdminChannel).Muted.Should().BeFalse("the mute named another channel");
    }

    [Fact]
    public async Task ConfbridgeMute_ShouldChangeNoOne_WhenItNamesNoChannel()
    {
        await StartAndGetObserverAsync();
        Join(AdminChannel, "No");

        Mute(null);

        _sut.MeetMe.GetRoom(Conference)!.UserCount.Should().Be(1, "a mute with no channel creates no participant");
        Participant(AdminChannel).Muted.Should().BeFalse("the mute named no channel");
    }
}
