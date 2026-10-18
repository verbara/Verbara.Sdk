namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.NetworkPartition;

using System.Diagnostics;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using AmiConnection = Verbara.Sdk.Ami.Connection.AmiConnection;

/// <summary>
/// Against a real Asterisk: a two-party bridge and a two-participant ConfBridge room that stay up across a cut of the
/// AMI connection are held by the live server after the reconnect's reload, as Asterisk's own listings report them.
/// </summary>
/// <remarks>
/// <para>
/// The connection under test runs through the Toxiproxy hop in front of Asterisk's AMI port and is cut with
/// <c>reset_peer</c>; the control connection goes straight to Asterisk and is the truth (<c>BridgeList</c>,
/// <c>ConfbridgeList</c>). Live learns the bridge and the room from their events before the cut. Measured on the
/// unfixed code with Asterisk 22.12, 23.6 and 24.0 rc2 on 2026-10-18: after the reload Live read 0
/// bridges and 0 rooms against truth, in 240 of 240 runs.
/// </para>
/// <para>
/// Ext 100 and ext 600 hang up on their own after <c>Wait(30)</c>; the cut and the reload fit well inside that.
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ReloadRestoresBridgeAndRoomTests : FunctionalTestBase
{
    private const string ProxyName = ToxiproxyFixture.AmiProxyName;
    private const string ToxicName = "reload-restores-bridge-and-room-cut";
    private const string HangupEverything = "channel request hangup all";
    private static readonly TimeSpan LearnBound = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReloadBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Reload_ShouldHoldTheBridgeAndTheRoomAsteriskReports_WhenBothAreUpAcrossAnAmiCut()
    {
        var conference = $"test-reload-room-{Guid.NewGuid():N}";
        await using var control = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await control.ConnectAsync();

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });
        await connection.ConnectAsync();
        await using var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();
        var elapsed = Stopwatch.StartNew();
        try
        {
            // One two-party bridge (ext 100 dialled from an originated Local leg) and one room of two participants.
            await control.SendActionAsync(new OriginateAction
            {
                Channel = "Local/100@test-functional/n",
                Application = "Dial",
                Data = "Local/100@test-functional/n,120",
                CallerId = "Reload Restore <5557778>",
                IsAsync = true,
                Timeout = 20000,
            });
            for (var i = 1; i <= 2; i++)
            {
                await control.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/600@test-functional/n",
                    Application = "ConfBridge",
                    Data = conference,
                    IsAsync = true,
                });
            }

            var learned = await WaitUntilAsync(
                () => server.Bridges.ActiveBridgeCount >= 2 && server.MeetMe.GetRoom(conference)?.UserCount == 2,
                LearnBound);
            learned.Should().BeTrue(
                "Live learns the bridge, the room's bridge and both participants from their events before the cut "
                + "(after {0}: {1} active bridges, {2} participants)",
                elapsed.Elapsed, server.Bridges.ActiveBridgeCount, server.MeetMe.GetRoom(conference)?.UserCount);
            var heldBefore = server.Bridges.ActiveBridges.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

            await ToxiproxyControl.AddToxicAsync(ProxyName, ToxicName, "reset_peer", "downstream",
                new Dictionary<string, object> { ["timeout"] = 0 });
            using (var downCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                while (connection.State == AmiConnectionState.Connected && !downCts.IsCancellationRequested)
                {
                    await BestEffort.SendAsync(connection, new PingAction(), downCts.Token);
                    // fence-allow: LOOP-DRIVER — paces the Ping loop that detects the cut; downCts bounds it
                    try { await Task.Delay(TimeSpan.FromMilliseconds(250), downCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            connection.State.Should().NotBe(AmiConnectionState.Connected, "the AMI connection under test is cut");
            await ToxiproxyControl.RemoveToxicAsync(ProxyName, ToxicName);
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

            // The truth, straight from Asterisk: the bridges Live held before the cut that are still up, and the room.
            var truthBridges = (await ListBridgeIdsAsync(control)).Where(heldBefore.Contains).ToHashSet(StringComparer.Ordinal);
            var truthParticipants = await ListParticipantsAsync(control, conference);
            truthBridges.Should().HaveCountGreaterThanOrEqualTo(2, "the two-party bridge and the room's bridge are still up");
            truthParticipants.Should().HaveCount(2, "both participants are still in the room");

            var restored = await WaitUntilAsync(
                () => truthBridges.All(id => server.Bridges.ActiveBridges.Any(b => b.Id == id))
                    && Participants(server, conference).SetEquals(truthParticipants),
                ReloadBound);

            new
            {
                Restored = restored,
                ActiveBridges = server.Bridges.ActiveBridges.Select(b => b.Id).Where(truthBridges.Contains).Order().ToArray(),
                RoomParticipants = Participants(server, conference).Order().ToArray(),
            }.Should().BeEquivalentTo(
                new
                {
                    Restored = true,
                    ActiveBridges = truthBridges.Order().ToArray(),
                    RoomParticipants = truthParticipants.Order().ToArray(),
                },
                "the bridge and the room are up in Asterisk across the cut, so after the reconnect's reload Live holds "
                + "every bridge BridgeList lists and the room with the participants ConfbridgeList lists (after {0})",
                elapsed.Elapsed);
        }
        finally
        {
            await ToxiproxyControl.TryRemoveToxicAsync(ProxyName, ToxicName);
            await BestEffort.SendAsync(control, new CommandAction { Command = HangupEverything });
            // fence-allow: SETTLE — lets the hangups finish before the next test in the collection
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static HashSet<string> Participants(VerbaraServer server, string conference) =>
        server.MeetMe.GetRoom(conference)?.Users.Keys.ToHashSet(StringComparer.Ordinal) ?? [];

    private static async Task<List<string>> ListBridgeIdsAsync(AmiConnection connection)
    {
        var ids = new List<string>();
        await foreach (var evt in connection.SendEventGeneratingActionAsync(new BridgeListAction()))
        {
            if (evt is BridgeListItemEvent { BridgeUniqueid: { Length: > 0 } id })
                ids.Add(id);
        }

        return ids;
    }

    private static async Task<HashSet<string>> ListParticipantsAsync(AmiConnection connection, string conference)
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var evt in connection.SendEventGeneratingActionAsync(new ConfbridgeListAction { Conference = conference }))
        {
            if (evt is ConfbridgeListEvent { Channel: { Length: > 0 } channel })
                channels.Add(channel);
        }

        return channels;
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
}
