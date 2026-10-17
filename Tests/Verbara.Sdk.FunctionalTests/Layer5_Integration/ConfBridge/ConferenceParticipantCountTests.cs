namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.ConfBridge;

using System.Collections.Concurrent;
using System.Diagnostics;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Two callers in one ConfBridge conference, tracked by <see cref="VerbaraServer.MeetMe"/> against a real Asterisk:
/// both count, the first kick keeps the room with the other caller, and the second kick removes it.
/// </summary>
/// <remarks>
/// Ext 600 hangs up on its own after <c>Wait(30)</c>, so every bounded wait below fits in under 25 s from the first
/// originate, and each failure message reports the elapsed time.
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ConferenceParticipantCountTests : FunctionalTestBase
{
    private static readonly TimeSpan JoinBound = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LeaveBound = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RoomBound = TimeSpan.FromSeconds(2);

    public ConferenceParticipantCountTests() : base("Verbara.Sdk.Ami")
    {
    }

    [Fact]
    public async Task MeetMe_ShouldCountEachCallerAndKeepRoomUntilLastLeaves_WhenTwoCallersJoinOneConfBridge()
    {
        var conference = $"test-conf-count-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        await using var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        // Subscribed after StartAsync, so this observer runs after the server's for the same event; the room is
        // still polled under a bound rather than read once on this observer's signal.
        var joins = new ConcurrentQueue<ConfbridgeJoinEvent>();
        var leaves = new ConcurrentQueue<ConfbridgeLeaveEvent>();
        using var subscription = connection.Subscribe(new ConferenceObserver(conference, joins.Enqueue, leaves.Enqueue));

        var elapsed = Stopwatch.StartNew();
        try
        {
            for (var i = 1; i <= 2; i++)
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/600@test-functional/n",
                    Application = "ConfBridge",
                    Data = conference,
                    IsAsync = true,
                    ActionId = $"conf-count-ch{i}"
                });
            }

            var bothJoined = await WaitUntilAsync(() => joins.Count >= 2, JoinBound);
            bothJoined.Should().BeTrue(
                "two ConfbridgeJoin events must arrive for the conference within {0}; {1} arrived after {2}",
                JoinBound, joins.Count, elapsed.Elapsed);

            var joinedChannels = joins.Select(j => j.Channel!).ToList();
            joinedChannels.Should().OnlyHaveUniqueItems("each originate joins on its own channel");

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference)?.UserCount == 2, RoomBound);
            var room = server.MeetMe.GetRoom(conference);
            room.Should().NotBeNull("both callers are in the conference (after {0})", elapsed.Elapsed);
            room!.UserCount.Should().Be(2,
                "two distinct channels joined the conference (after {0})", elapsed.Elapsed);
            room.Users.Values.Select(u => u.Channel).Should().BeEquivalentTo(joinedChannels,
                "the room holds exactly the two joined channels (after {0})", elapsed.Elapsed);

            // Kick the first caller: the room keeps the other.
            var first = joinedChannels[0];
            var second = joinedChannels[1];
            await connection.SendActionAsync(new ConfbridgeKickAction { Conference = conference, Channel = first });

            var firstLeft = await WaitUntilAsync(() => leaves.Any(l => l.Channel == first), LeaveBound);
            firstLeft.Should().BeTrue(
                "kicking {0} must produce a ConfbridgeLeave naming it within {1} (after {2})",
                first, LeaveBound, elapsed.Elapsed);

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference)?.UserCount == 1, RoomBound);
            (server.MeetMe.GetRoom(conference)?.UserCount).Should().Be(1,
                "one caller is still in the conference after the first kick (after {0})", elapsed.Elapsed);
            server.MeetMe.GetRoom(conference)!.Users.Values.Select(u => u.Channel).Should().BeEquivalentTo(
                [second], "the remaining participant is the caller that was not kicked (after {0})", elapsed.Elapsed);

            // Kick the other caller: the room is gone.
            await connection.SendActionAsync(new ConfbridgeKickAction { Conference = conference, Channel = second });

            var secondLeft = await WaitUntilAsync(() => leaves.Any(l => l.Channel == second), LeaveBound);
            secondLeft.Should().BeTrue(
                "kicking {0} must produce a ConfbridgeLeave naming it within {1} (after {2})",
                second, LeaveBound, elapsed.Elapsed);

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference) is null, RoomBound);
            server.MeetMe.GetRoom(conference).Should().BeNull(
                "the last caller left the conference (after {0})", elapsed.Elapsed);
        }
        finally
        {
            await BestEffort.SendAsync(connection, new CommandAction { Command = "channel request hangup all" });
            await Task.Delay(TimeSpan.FromSeconds(1)); // fence-allow: SETTLE — lets the hangups finish before the next test in the collection
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan bound)
    {
        var deadline = Stopwatch.StartNew();
        // fence-allow: GUARD-TIMEOUT — failure bound on an observed condition; a green run leaves on the condition
        while (deadline.Elapsed < bound)
        {
            if (condition())
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(50)); // fence-allow: LOOP-DRIVER — paces the re-check until the condition holds
        }

        return condition();
    }

    /// <summary>Collects the ConfBridge join and leave events of one conference.</summary>
    private sealed class ConferenceObserver(
        string conference,
        Action<ConfbridgeJoinEvent> onJoin,
        Action<ConfbridgeLeaveEvent> onLeave) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case ConfbridgeJoinEvent e when e.Conference == conference: onJoin(e); break;
                case ConfbridgeLeaveEvent e when e.Conference == conference: onLeave(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
