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
/// <para>
/// Ext 600 hangs up on its own after <c>Wait(30)</c>, so every bounded wait below fits in under 25 s from the first
/// originate (two joins, two mutes and four state polls: 22 s at most), and each failure message reports the elapsed
/// time. The pings sent while an event is awaited are answered in milliseconds; one that is never answered fails the
/// test on the connection's response timeout instead of extending a wait.
/// </para>
/// <para>
/// <b>Why an event wait pings.</b> Asterisk's manager can hold an event queued for a session until that session sends
/// something: its session thread publishes itself as the thread to wake, releases the lock and only then enters
/// <c>poll()</c>, so an event queued in that gap signals a thread that is not yet waiting, and <c>poll()</c> then blocks
/// with no timeout. Measured by toggling one participant's mute with no other traffic on the session: 5 events in
/// about 5.4 million on Asterisk 23, and 7 in about 3.9 million on Asterisk 22, were still undelivered 500 ms after
/// Asterisk answered Success, and each arrived within 2 ms of the next Ping. So the action's response is asserted (a
/// refusal fails on Asterisk's own message), and each event wait sends a Ping every <see cref="NudgeInterval"/> until
/// the event arrives; a green run that sees its event within that interval sends none. Asterisk 22.11.0 and 23.5.0
/// replaced that wake-up with a per-session alert pipe, which closes the gap; the Ping stays for the builds before
/// them (measured on 22.10.1 and 23.4.1).
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ConferenceParticipantStateTests : FunctionalTestBase
{
    private static readonly TimeSpan JoinBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MuteBound = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StateBound = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan NudgeInterval = TimeSpan.FromMilliseconds(250);

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

        // Any input on the session makes Asterisk flush the events it holds for it (see the remarks).
        Func<Task> nudge = () => connection.SendActionAsync(new PingAction()).AsTask();

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
            var mutedJoined = await WaitUntilAsync(() => !joins.IsEmpty, JoinBound, nudge);
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
            var bothJoined = await WaitUntilAsync(() => joins.Count >= 2, JoinBound, nudge);
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
            var muteResponse = await connection.SendActionAsync(
                new ConfbridgeMuteAction { Conference = conference, Channel = otherChannel });
            muteResponse.Response.Should().Be("Success",
                "Asterisk must accept muting {0}; it answered {1}: {2} (after {3})",
                otherChannel, muteResponse.Response, muteResponse.Message, elapsed.Elapsed);
            var muteArrived = await WaitUntilAsync(() => mutes.Any(m => m.Channel == otherChannel), MuteBound, nudge);
            muteArrived.Should().BeTrue(
                "muting {0} must produce a ConfbridgeMute naming it within {1} (after {2})",
                otherChannel, MuteBound, elapsed.Elapsed);

            await WaitUntilAsync(() => server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(otherChannel)?.Muted == true, StateBound);
            (server.MeetMe.GetRoom(conference)?.Users.GetValueOrDefault(otherChannel)?.Muted).Should().BeTrue(
                "ConfbridgeMute named this caller (after {0})", elapsed.Elapsed);

            // Unmute it again.
            var unmuteResponse = await connection.SendActionAsync(
                new ConfbridgeUnmuteAction { Conference = conference, Channel = otherChannel });
            unmuteResponse.Response.Should().Be("Success",
                "Asterisk must accept unmuting {0}; it answered {1}: {2} (after {3})",
                otherChannel, unmuteResponse.Response, unmuteResponse.Message, elapsed.Elapsed);
            var unmuteArrived = await WaitUntilAsync(() => unmutes.Any(m => m.Channel == otherChannel), MuteBound, nudge);
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

    /// <summary>
    /// Re-checks <paramref name="condition"/> until it holds or <paramref name="bound"/> passes; when
    /// <paramref name="nudge"/> is given, runs it every <see cref="NudgeInterval"/> the condition still does not hold.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan bound, Func<Task>? nudge = null)
    {
        var deadline = Stopwatch.StartNew();
        var lastNudge = TimeSpan.Zero;
        // fence-allow: GUARD-TIMEOUT — failure bound on an observed condition; a green run leaves on the condition
        while (deadline.Elapsed < bound)
        {
            if (condition())
                return true;
            if (nudge is not null && deadline.Elapsed - lastNudge >= NudgeInterval)
            {
                lastNudge = deadline.Elapsed;
                await nudge();
            }

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
