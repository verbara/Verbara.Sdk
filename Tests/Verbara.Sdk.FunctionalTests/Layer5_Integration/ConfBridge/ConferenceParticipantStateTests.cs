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
/// the caller that enters with the start-muted <c>muted_user</c> profile reads muted from its join, and the caller
/// with the default profile reads muted after a manager mute and not muted after the unmute.
/// </summary>
/// <remarks>
/// Ext 600 hangs up on its own after <c>Wait(30)</c>, so every bounded wait below fits in under 25 s from the first
/// originate (two joins, two mutes and four state polls: 22 s at most), and each failure message reports the elapsed
/// time.
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ConferenceParticipantStateTests : FunctionalTestBase
{
    private static readonly TimeSpan JoinBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MuteBound = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StateBound = TimeSpan.FromSeconds(1.5);

    public ConferenceParticipantStateTests() : base("Verbara.Sdk.Ami")
    {
    }

    [Fact]
    public async Task MeetMe_ShouldReflectStartMutedJoinAndManagerMuteAndUnmute_WhenTwoCallersJoinOneConfBridge()
    {
        var conference = $"test-conf-state-{Guid.NewGuid():N}";

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
        var mutes = new ConcurrentQueue<ConfbridgeMuteEvent>();
        var unmutes = new ConcurrentQueue<ConfbridgeUnmuteEvent>();
        using var subscription = connection.Subscribe(
            new ConferenceObserver(conference, joins.Enqueue, mutes.Enqueue, unmutes.Enqueue));

        var elapsed = Stopwatch.StartNew();
        try
        {
            // The first caller enters with the start-muted user profile, the second with the default one.
            await connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/600@test-functional/n",
                Application = "ConfBridge",
                Data = $"{conference},default_bridge,muted_user",
                IsAsync = true,
                ActionId = "conf-state-muted"
            });
            var mutedJoined = await WaitUntilAsync(() => !joins.IsEmpty, JoinBound);
            mutedJoined.Should().BeTrue(
                "the start-muted caller's ConfbridgeJoin must arrive within {0} (after {1})", JoinBound, elapsed.Elapsed);
            var mutedChannel = joins.First().Channel!;
            joins.First().Muted.Should().Be("Yes",
                "Asterisk sends Muted: Yes on the join of a startmuted=yes profile (after {0})", elapsed.Elapsed);

            await connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/600@test-functional/n",
                Application = "ConfBridge",
                Data = conference,
                IsAsync = true,
                ActionId = "conf-state-default"
            });
            var bothJoined = await WaitUntilAsync(() => joins.Count >= 2, JoinBound);
            bothJoined.Should().BeTrue(
                "two ConfbridgeJoin events must arrive for the conference within {0}; {1} arrived after {2}",
                JoinBound, joins.Count, elapsed.Elapsed);
            var otherChannel = joins.Select(j => j.Channel!).Single(c => c != mutedChannel);

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference)?.UserCount == 2, StateBound);
            var room = server.MeetMe.GetRoom(conference);
            room.Should().NotBeNull("both callers are in the conference (after {0})", elapsed.Elapsed);
            room!.Users.Keys.Should().BeEquivalentTo([mutedChannel, otherChannel],
                "the room holds exactly the two joined channels (after {0})", elapsed.Elapsed);

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(mutedChannel)?.Muted == true, StateBound);
            (server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(mutedChannel)?.Muted).Should().BeTrue(
                "the caller that joined with Muted: Yes reads muted from its join (after {0})", elapsed.Elapsed);
            (server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(otherChannel)?.Muted).Should().BeFalse(
                "the caller that joined with Muted: No reads not muted (after {0})", elapsed.Elapsed);

            // Mute the default caller through the manager interface.
            await connection.SendActionAsync(new ConfbridgeMuteAction { Conference = conference, Channel = otherChannel });
            var muteArrived = await WaitUntilAsync(() => mutes.Any(m => m.Channel == otherChannel), MuteBound);
            muteArrived.Should().BeTrue(
                "muting {0} must produce a ConfbridgeMute naming it within {1} (after {2})",
                otherChannel, MuteBound, elapsed.Elapsed);

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(otherChannel)?.Muted == true, StateBound);
            (server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(otherChannel)?.Muted).Should().BeTrue(
                "ConfbridgeMute named this caller (after {0})", elapsed.Elapsed);

            // Unmute it again.
            await connection.SendActionAsync(new ConfbridgeUnmuteAction { Conference = conference, Channel = otherChannel });
            var unmuteArrived = await WaitUntilAsync(() => unmutes.Any(m => m.Channel == otherChannel), MuteBound);
            unmuteArrived.Should().BeTrue(
                "unmuting {0} must produce a ConfbridgeUnmute naming it within {1} (after {2})",
                otherChannel, MuteBound, elapsed.Elapsed);

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(otherChannel)?.Muted == false, StateBound);
            (server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(otherChannel)?.Muted).Should().BeFalse(
                "ConfbridgeUnmute named this caller (after {0})", elapsed.Elapsed);
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

    /// <summary>Collects the ConfBridge join, mute and unmute events of one conference.</summary>
    private sealed class ConferenceObserver(
        string conference,
        Action<ConfbridgeJoinEvent> onJoin,
        Action<ConfbridgeMuteEvent> onMute,
        Action<ConfbridgeUnmuteEvent> onUnmute) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case ConfbridgeJoinEvent e when e.Conference == conference: onJoin(e); break;
                case ConfbridgeMuteEvent e when e.Conference == conference: onMute(e); break;
                case ConfbridgeUnmuteEvent e when e.Conference == conference: onUnmute(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
