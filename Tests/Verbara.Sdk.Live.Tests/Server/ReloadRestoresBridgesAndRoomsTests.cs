using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A reload restores the bridge and conference tables from Asterisk's listings, instead of leaving them cleared: a bridge
/// or room up across a reconnect is held afterwards, silently and uncounted; one the completed listing omits is removed
/// with its removal event; <c>No active conferences.</c> is an empty listing and <c>Permission denied</c> a refusal that
/// changes nothing and warns once per AMI session; and a refused <c>Status</c> leaves both tables alone, unasked.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>. Live learns the
/// bridges and rooms from the first session's events, as it does in production; the session then ends, and the
/// reconnect's reload reads the second peer's listings. Every frame has the headers Asterisk 22.12, 23.6 and 24.0 rc2
/// sent in recordings captured on 2026-10-18: <c>BridgeListItem</c>, <c>ConfbridgeListRooms</c>,
/// <c>ConfbridgeList</c>, a bridged <c>Status</c> leg whose <c>BridgeID</c> equals the bridge's
/// <c>BridgeUniqueid</c>, and the conference listings' <c>No active conferences.</c> and <c>Permission denied</c>.
/// </para>
/// <para>
/// The bridge counters are read with <see cref="LiveCounters"/>, so the class runs in <see cref="LiveCountersGroup"/>.
/// </para>
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class ReloadRestoresBridgesAndRoomsTests
{
    private const string StateLoaded = "[LIVE] State loaded";
    private const string Room = "capture-room";
    private const string RoomBridge = "22222222-2222-2222-2222-222222222222";

    private static readonly TwoParty BridgeX = new("11111111-1111-1111-1111-000000000001",
        new Leg("1792274553.2", "Local/caller@capture-00000001;1", "1792274553.2"),
        new Leg("1792274553.4", "Local/agent@capture-00000002;1", "1792274553.2"));

    private static readonly TwoParty BridgeY = new("11111111-1111-1111-1111-000000000002",
        new Leg("1792274553.6", "Local/caller@capture-00000004;1", "1792274553.6"),
        new Leg("1792274553.8", "Local/agent@capture-00000005;1", "1792274553.6"));

    private static readonly Leg[] Participants =
    [
        new("1792274561.7", "Local/conf@capture-00000003;2", "1792274561.6"),
        new("1792274561.9", "Local/conf@capture-00000006;2", "1792274561.8"),
        new("1792274561.11", "Local/conf@capture-00000007;2", "1792274561.10"),
    ];

    private static readonly Leg Announcer = new("1792274561.5", "CBAnn/capture-room-00000001;1", "1792274561.5");

    // ---- The reload restores the bridge and conference tables ----

    [Fact]
    public async Task Reload_ShouldRestoreTheBridgesAndTheRoomWithoutCountingOrAnnouncingThem_WhenTheyAreUpAcrossAReconnect()
    {
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [.. Legs(BridgeX), .. Legs(BridgeY), .. RoomLegs(Participants)],
            Bridges = [Listed(BridgeX), Listed(BridgeY), RoomListed(Participants.Length)],
            Rooms = [new ListedRoom(Room, Participants.Select(Listed).ToArray())],
        };
        await using var rig = await Rig.StartAsync(second);
        await rig.LearnAsync([BridgeX, BridgeY], Participants);
        using var counters = new LiveCounters();
        var seen = rig.Watch();

        await rig.ReconnectAsync();

        var server = rig.Run.Server;
        using (new AssertionScope())
        {
            new
            {
                ActiveBridges = server.Bridges.ActiveBridgeCount,
                X = Members(server, BridgeX.Id),
                Y = Members(server, BridgeY.Id),
                RoomBridge = Members(server, RoomBridge),
                RoomParticipants = RoomParticipants(server),
                counters.BridgesCreated,
                PeerFault = second.Fault?.Message,
            }.Should().BeEquivalentTo(
                new
                {
                    ActiveBridges = 3,
                    X = Uids(BridgeX.Caller, BridgeX.Agent),
                    Y = Uids(BridgeY.Caller, BridgeY.Agent),
                    RoomBridge = Uids(Participants),
                    RoomParticipants = Participants.Select(p => p.Channel).Order().ToArray(),
                    BridgesCreated = 0L,
                    PeerFault = default(string),
                },
                "two two-party bridges and a three-participant room are up in Asterisk before and after the reconnect; the "
                + "reload restores both tables from BridgeList, Status's BridgeID and ConfbridgeList, raises no created, "
                + "entered or joined event for what it only read, and counts no creation for a bridge it already held. "
                + $"Measured: {counters}; {seen}");
            new { seen.BridgesCreated, seen.ChannelsEntered, seen.UsersJoined }.Should().BeEquivalentTo(
                new { BridgesCreated = 0, ChannelsEntered = 0, UsersJoined = 0 },
                "an entry the reload only read from a snapshot raises no BridgeCreated, ChannelEntered or UserJoined, so "
                + "Sessions does not publish a Connected from a reload");
        }
    }

    [Fact]
    public async Task Reload_ShouldRemoveAndAnnounceABridge_WhenTheCompletedListingNoLongerListsIt()
    {
        // Bridge Y was hung up during the outage: neither BridgeList nor Status lists it or its legs.
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [.. Legs(BridgeX)],
            Bridges = [Listed(BridgeX)],
        };
        await using var rig = await Rig.StartAsync(second);
        await rig.LearnAsync([BridgeX, BridgeY], []);
        using var counters = new LiveCounters();
        var seen = rig.Watch();

        await rig.ReconnectAsync();

        var server = rig.Run.Server;
        new
        {
            ActiveBridges = server.Bridges.ActiveBridgeCount,
            XHeld = server.Bridges.GetById(BridgeX.Id) is not null,
            Destroyed = seen.DestroyedIds,
            counters.BridgesDestroyed,
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new
            {
                ActiveBridges = 1,
                XHeld = true,
                Destroyed = new[] { BridgeY.Id },
                BridgesDestroyed = 1L,
                PeerFault = default(string),
            },
            "the completed BridgeList omits bridge Y: it is removed, BridgeDestroyed is raised for it so Sessions clears its "
            + $"BridgeId, live.bridges.destroyed advances by one, and bridge X, still listed, is kept. Measured: {counters}; {seen}");
    }

    [Fact]
    public async Task Reload_ShouldReadNoActiveConferencesAsAnEmptyListing_WhenTheRoomEndedDuringTheOutage()
    {
        var second = new BootingAsterisk { BootedAtLogin = true, Rooms = [] };
        await using var rig = await Rig.StartAsync(second);
        await rig.LearnAsync([], Participants);

        await rig.ReconnectAsync();

        new
        {
            RoomHeld = rig.Run.Server.MeetMe.GetRoom(Room) is not null,
            ListingAsked = second.Asked("ConfbridgeListRooms"),
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new { RoomHeld = false, ListingAsked = 1, PeerFault = default(string) },
            "the room ended during the outage; the reload asks ConfbridgeListRooms once, reads Asterisk's 'Response: "
            + "Error' / 'No active conferences.' as a complete, empty listing, and removes the held room because the "
            + "listing says so, not because the reload cleared the table");
    }

    [Fact]
    public async Task Load_ShouldKeepTheRoomsAndWarnOncePerAmiSession_WhenTheConferenceListingIsRefused()
    {
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [.. RoomLegs(Participants)],
            Bridges = [RoomListed(Participants.Length)],
            ConferenceListing = ConferenceListing.PermissionDenied,
        };
        await using var rig = await Rig.StartAsync(second, firstListing: ConferenceListing.PermissionDenied);
        await rig.LearnAsync([], Participants);

        // Two loads in the first AMI session, then the reconnect's reload in the second.
        await rig.Run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        await rig.Run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        await rig.ReconnectAsync(loadsBefore: 3);

        var refusals = rig.Run.ServerLog.Entries
            .Where(e => e.Line.Contains("ConfbridgeList", StringComparison.Ordinal)
                && e.Line.Contains(BootingAsterisk.PermissionDenied, StringComparison.Ordinal))
            .Select(e => e.Level)
            .ToArray();
        new
        {
            RoomParticipants = RoomParticipants(rig.Run.Server),
            WarningRefusals = refusals.Count(l => l == LogLevel.Warning),
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new
            {
                RoomParticipants = Participants.Select(p => p.Channel).Order().ToArray(),
                WarningRefusals = 2,
                PeerFault = default(string),
            },
            "Asterisk refuses both conference listings with 'Permission denied' (an AMI user without reporting): a refusal "
            + "is not an empty listing, so the room keeps its three participants across both loads and the reconnect, and "
            + "the refusal, which names the listing, is a Warning once per AMI session (the first session's first load and "
            + "the reload) and Debug after that. Log: "
            + string.Join(" | ", rig.Run.ServerLog.Entries.Select(e => $"{e.Level}: {e.Line}")));
    }

    [Fact]
    public async Task Reload_ShouldNeitherAskForNorTouchBridgesAndRooms_WhenAsteriskRefusesStatus()
    {
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusRefusedFromAsk = 1,
            Bridges = [],
            Rooms = [],
        };
        await using var rig = await Rig.StartAsync(second);
        await rig.LearnAsync([BridgeX], Participants);

        await rig.ReconnectAsync();

        var server = rig.Run.Server;
        new
        {
            BridgeListAsked = second.Asked("BridgeList"),
            RoomsAsked = second.Asked("ConfbridgeListRooms"),
            RoomAsked = second.Asked("ConfbridgeList"),
            XMembers = Members(server, BridgeX.Id),
            RoomParticipants = RoomParticipants(server),
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new
            {
                BridgeListAsked = 0,
                RoomsAsked = 0,
                RoomAsked = 0,
                XMembers = Uids(BridgeX.Caller, BridgeX.Agent),
                RoomParticipants = Participants.Select(p => p.Channel).Order().ToArray(),
                PeerFault = default(string),
            },
            "with Status refused the channels are stale, and a bridge table reconciled over stale channels is worse than "
            + "none: the reload asks for no bridge or conference listing, and both tables keep what they held");
    }

    [Fact]
    public async Task Reload_ShouldRestoreTheRoomFromItsListingAlone_WhenTheConferenceBridgeCarriesTheAnnouncer()
    {
        var two = Participants[..2];
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [.. RoomLegs(two), Status(Announcer, RoomBridge)],
            Bridges = [RoomListed(two.Length + 1)],
            Rooms = [new ListedRoom(Room, two.Select(Listed).ToArray())],
        };
        await using var rig = await Rig.StartAsync(second);
        await rig.LearnAsync([], two, announcer: true);

        await rig.ReconnectAsync();

        new
        {
            RoomParticipants = RoomParticipants(rig.Run.Server),
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new { RoomParticipants = two.Select(p => p.Channel).Order().ToArray(), PeerFault = default(string) },
            "the softmix bridge lists three channels (BridgeNumChannels: 3), one of them the CBAnn announcer whose Status "
            + "BridgeID names the room's bridge; the room is restored from ConfbridgeList's rows, so it holds exactly the "
            + "two listed participants, and no participant is made from the announcer");
    }

    // ---- A participant a reconciliation removes raises UserLeft ----

    [Fact]
    public async Task Reload_ShouldRaiseUserLeftOnceForTheParticipantTheListingNoLongerReports_WhenItsRoomIsStillListed()
    {
        var stayed = Participants[..2];
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [.. RoomLegs(stayed)],
            Bridges = [RoomListed(stayed.Length)],
            Rooms = [new ListedRoom(Room, stayed.Select(Listed).ToArray())],
        };
        await using var rig = await Rig.StartAsync(second);
        await rig.LearnAsync([], Participants);
        var seen = rig.Watch();

        await rig.ReconnectAsync();

        new
        {
            UsersLeft = seen.UsersLeft,
            RoomParticipants = RoomParticipants(rig.Run.Server),
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new
            {
                UsersLeft = new[] { Participants[2].Channel },
                RoomParticipants = stayed.Select(p => p.Channel).Order().ToArray(),
                PeerFault = default(string),
            },
            "one participant left the room during the outage and the completed ConfbridgeList reports the other two: "
            + "the reconciliation removes that participant and raises UserLeft once, for it alone, as a room's observed "
            + $"end does for each participant it holds. Measured: {seen}");
    }

    [Fact]
    public async Task Reload_ShouldRaiseUserLeftForEveryParticipant_WhenTheListingAnswersNoActiveConferences()
    {
        var second = new BootingAsterisk { BootedAtLogin = true, Rooms = [] };
        await using var rig = await Rig.StartAsync(second);
        await rig.LearnAsync([], Participants);
        var seen = rig.Watch();

        await rig.ReconnectAsync();

        new
        {
            UsersLeft = seen.UsersLeft.Order().ToArray(),
            RoomHeld = rig.Run.Server.MeetMe.GetRoom(Room) is not null,
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new
            {
                UsersLeft = Participants.Select(p => p.Channel).Order().ToArray(),
                RoomHeld = false,
                PeerFault = default(string),
            },
            "the room ended during the outage and the listing answers 'No active conferences.': the reconciliation "
            + $"removes the room and raises UserLeft once for each of its three participants. Measured: {seen}");
    }

    // ---- The rig ----

    private sealed record Leg(string UniqueId, string Channel, string LinkedId);

    private sealed record TwoParty(string Id, Leg Caller, Leg Agent);

    private static IEnumerable<StatusChannel> Legs(TwoParty bridge) =>
        [Status(bridge.Caller, bridge.Id), Status(bridge.Agent, bridge.Id)];

    private static IEnumerable<StatusChannel> RoomLegs(IEnumerable<Leg> participants) =>
        participants.Select(p => Status(p, RoomBridge));

    private static StatusChannel Status(Leg leg, string bridgeId) =>
        new(leg.UniqueId, leg.Channel, leg.LinkedId, BridgeId: bridgeId);

    private static ListedBridge Listed(TwoParty bridge) => new(bridge.Id, NumChannels: 2);

    private static ListedBridge RoomListed(int numChannels) =>
        new(RoomBridge, numChannels, Type: "base", Technology: "softmix", Creator: "ConfBridge", Name: Room);

    private static ListedParticipant Listed(Leg leg) => new(leg.Channel, leg.UniqueId, leg.LinkedId);

    private static string[] Uids(params IEnumerable<Leg> legs) => legs.Select(l => l.UniqueId).Order().ToArray();

    private static string[] Members(VerbaraServer server, string bridgeId) =>
        server.Bridges.GetById(bridgeId)?.Channels.Keys.Order().ToArray() ?? [];

    private static string[] RoomParticipants(VerbaraServer server) =>
        server.MeetMe.GetRoom(Room)?.Users.Keys.Order().ToArray() ?? [];

    /// <summary>What the server raised from the moment <see cref="Rig.Watch"/> was called.</summary>
    private sealed class Seen
    {
        private int _bridgesCreated;
        private int _channelsEntered;
        private int _usersJoined;
        private readonly List<string> _destroyed = [];
        private readonly List<string> _usersLeft = [];
        private readonly Lock _gate = new();

        public int BridgesCreated => Volatile.Read(ref _bridgesCreated);

        public int ChannelsEntered => Volatile.Read(ref _channelsEntered);

        public int UsersJoined => Volatile.Read(ref _usersJoined);

        public string[] DestroyedIds
        {
            get
            {
                lock (_gate)
                    return [.. _destroyed];
            }
        }

        public string[] UsersLeft
        {
            get
            {
                lock (_gate)
                    return [.. _usersLeft];
            }
        }

        public void Attach(VerbaraServer server)
        {
            server.Bridges.BridgeCreated += _ => Interlocked.Increment(ref _bridgesCreated);
            server.Bridges.ChannelEntered += (_, _) => Interlocked.Increment(ref _channelsEntered);
            server.MeetMe.UserJoined += (_, _) => Interlocked.Increment(ref _usersJoined);
            server.Bridges.BridgeDestroyed += b =>
            {
                lock (_gate)
                    _destroyed.Add(b.BridgeUniqueid);
            };
            server.MeetMe.UserLeft += (_, u) =>
            {
                lock (_gate)
                    _usersLeft.Add(u.Channel);
            };
        }

        public override string ToString() =>
            $"created={BridgesCreated} entered={ChannelsEntered} joined={UsersJoined} "
            + $"destroyed=[{string.Join(",", DestroyedIds)}] left=[{string.Join(",", UsersLeft)}]";
    }

    /// <summary>
    /// A run whose first session Live learns the bridges and the room from, by their events, and whose second session
    /// the reconnect's reload reads.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(Run run, BootingAsterisk first)
        {
            Run = run;
            First = first;
        }

        public Run Run { get; }

        public BootingAsterisk First { get; }

        /// <summary>
        /// Starts the server over a first peer that lists nothing at the start's load (Live learns the tables from the
        /// events that follow), with <paramref name="firstListing"/> for its conference listings.
        /// </summary>
        public static async Task<Rig> StartAsync(BootingAsterisk second,
            ConferenceListing firstListing = ConferenceListing.Listed)
        {
            var first = new BootingAsterisk { BootedAtLogin = true, ConferenceListing = firstListing };
            var run = await Run.ConnectAsync(first, second, autoReconnect: true);
            try
            {
                await run.StartServerAsync();
                await run.ServerLog.Logged(StateLoaded, 1).WaitAsync(Run.Bound);
                return new Rig(run, first);
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// The first peer reports the two-party bridges and the room as Asterisk does when they are built — each leg's
        /// <c>Newchannel</c>, <c>BridgeCreate</c>, each <c>BridgeEnter</c>, and each participant's
        /// <c>ConfbridgeJoin</c> — and the test waits until Live has applied every one.
        /// </summary>
        public async Task LearnAsync(IReadOnlyList<TwoParty> bridges, IReadOnlyList<Leg> participants,
            bool announcer = false)
        {
            var entries = (bridges.Count * 2) + participants.Count + (announcer ? 1 : 0);
            var entered = Counted(entries, h => Run.Server.Bridges.ChannelEntered += (_, _) => h());
            var joined = Counted(participants.Count, h => Run.Server.MeetMe.UserJoined += (_, _) => h());

            foreach (var bridge in bridges)
            {
                await Send("Newchannel", AsteriskFrames.Newchannel(bridge.Caller.UniqueId, bridge.Caller.Channel, bridge.Caller.LinkedId));
                await Send("Newchannel", AsteriskFrames.Newchannel(bridge.Agent.UniqueId, bridge.Agent.Channel, bridge.Agent.LinkedId));
                await Send("BridgeCreate", AsteriskFrames.BridgeCreate(bridge.Id));
                await Send("BridgeEnter", AsteriskFrames.BridgeEnter(bridge.Id, 1, bridge.Agent.UniqueId, bridge.Agent.Channel, bridge.Agent.LinkedId));
                await Send("BridgeEnter", AsteriskFrames.BridgeEnter(bridge.Id, 2, bridge.Caller.UniqueId, bridge.Caller.Channel, bridge.Caller.LinkedId));
            }

            if (participants.Count > 0)
            {
                await Send("BridgeCreate", AsteriskFrames.BridgeCreate(RoomBridge, "base", "softmix", "ConfBridge", Room));
                var inBridge = 0;
                if (announcer)
                {
                    await Send("Newchannel", AsteriskFrames.Newchannel(Announcer.UniqueId, Announcer.Channel, Announcer.LinkedId));
                    await Send("BridgeEnter", AsteriskFrames.BridgeEnter(RoomBridge, ++inBridge, Announcer.UniqueId,
                        Announcer.Channel, Announcer.LinkedId, "base", "softmix"));
                }

                foreach (var p in participants)
                {
                    await Send("Newchannel", AsteriskFrames.Newchannel(p.UniqueId, p.Channel, p.LinkedId));
                    await Send("BridgeEnter", AsteriskFrames.BridgeEnter(RoomBridge, ++inBridge, p.UniqueId, p.Channel,
                        p.LinkedId, "base", "softmix"));
                    await Send("ConfbridgeJoin", AsteriskFrames.ConfbridgeJoin(Room, RoomBridge, inBridge, p.UniqueId,
                        p.Channel, p.LinkedId));
                }
            }

            await entered.WaitAsync(Run.Bound);
            await joined.WaitAsync(Run.Bound);
        }

        /// <summary>Starts recording what the server raises from now on.</summary>
        public Seen Watch()
        {
            var seen = new Seen();
            seen.Attach(Run.Server);
            return seen;
        }

        /// <summary>
        /// The first session ends; the reconnect loop opens the second, and the test waits for the reload's
        /// <c>[LIVE] State loaded</c>, the one after <paramref name="loadsBefore"/>.
        /// </summary>
        public async Task ReconnectAsync(int loadsBefore = 1)
        {
            First.CloseSession();
            await Run.SecondSocketCreated.WaitAsync(Run.Bound);
            Run.ReleaseSecondPeer();
            await Run.ServerLog.Logged(StateLoaded, loadsBefore + 1).WaitAsync(Run.Bound);
        }

        public ValueTask DisposeAsync() => Run.DisposeAsync();

        private async Task Send(string eventType, KeyValuePair<string, string>[] fields)
        {
            if (!await First.SendEventAsync(eventType, fields))
                throw new InvalidOperationException($"The first session ended before the peer could report {eventType}.");
        }

        private static Task Counted(int times, Action<Action> subscribe)
        {
            if (times == 0)
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            subscribe(() =>
            {
                if (Interlocked.Increment(ref count) == times)
                    signal.TrySetResult();
            });
            return signal.Task;
        }
    }
}
