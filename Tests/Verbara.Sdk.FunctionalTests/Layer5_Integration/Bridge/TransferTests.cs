namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Bridge;

using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Events.Base;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tests for blind transfers (RedirectAction) and attended transfers (AtxferAction).
///
/// Blind transfers use RedirectAction to move a channel to a new dialplan extension.
/// Attended transfers use AtxferAction to initiate a consultation call before completing
/// the transfer. AtxferAction requires an active bridged channel with transfer features
/// enabled, which limits what can be tested in an automated environment.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class TransferTests : FunctionalTestBase
{
    public TransferTests() : base("Verbara.Sdk.Ami")
    {
    }

    // ─────────────────────────────────────────────────────────────
    //  Blind Transfer Tests (RedirectAction)
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// RedirectAction should move a live channel to a new extension.
    /// After redirect, the channel should execute the target extension's dialplan.
    /// </summary>
    [Fact]
    public async Task BlindTransfer_ShouldRedirectChannel()
    {
        // NewExten belongs to the dialplan class, which the default test user does not read.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, UseDialplanFlowUser);
        await connection.ConnectAsync();

        // The originate names its channel, so every event below is read for this call only.
        var callId = NewCallId("blind-redirect");
        var redirectSent = 0;
        var holding = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached161 = new TaskCompletionSource<NewExtenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new TransferEventObserver(
            onNewExten: e =>
            {
                if (e.UniqueId != callId || e.Channel is null)
                    return;
                // Measured on Asterisk 23: the originated ;1 half reports ext 100's Wait and no Answer.
                if (e.Application == "Wait" && e.Extension == "100")
                    holding.TrySetResult(e.Channel);
                if (e.Extension == "161" && Volatile.Read(ref redirectSent) == 1)
                    reached161.TrySetResult(e);
            }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            ChannelId = callId,
            ActionId = "blind-redirect-01"
        });

        var hold = await Task.WhenAny(holding.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        hold.Should().Be(holding.Task, "the originated channel runs ext 100, which reports a NewExten for its Wait within 10 s");
        var targetChannel = await holding.Task;

        // Redirect the channel to ext 161 (Answer + Wait(10))
        Volatile.Write(ref redirectSent, 1);
        var redirect = new RedirectAction
        {
            Channel = targetChannel,
            Context = "test-functional",
            Exten = "161",
            Priority = 1
        };
        await connection.SendActionAsync(redirect);

        // Only the redirect can put this call in ext 161: it was running ext 100's Wait(30).
        var reached = await Task.WhenAny(reached161.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        reached.Should().Be(reached161.Task,
            "RedirectAction to ext 161 must make the redirected channel run ext 161, reported as a NewExten on that channel within 5 s");
        var newExten = await reached161.Task;
        newExten.Channel.Should().Be(targetChannel, "the NewExten for ext 161 must come from the channel that was redirected");
        (newExten.RawFields?.GetValueOrDefault("Context")).Should().Be(redirect.Context, "the NewExten must be in the context the redirect named");
    }

    /// <summary>
    /// A BlindTransfer action on a party of a two-party bridge sends the other party to the target extension and
    /// fires a BlindTransferEvent. The bridge is a Dial between two Local channels (ext 170 dials ext 171): a
    /// ConfBridge cannot be blind-transferred — Asterisk 23 answers a BlindTransfer on a ConfBridge party with
    /// "Transfer invalid".
    /// </summary>
    [Fact]
    public async Task BlindTransfer_ShouldFireBlindTransferEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var callId = NewCallId("blind-transfer-evt");
        var blindTransfer = new TaskCompletionSource<BlindTransferEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridged = new TaskCompletionSource<BridgeEnterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new TransferEventObserver(
            onBlindTransfer: e =>
            {
                if (e.TransfererUniqueId == callId)
                    blindTransfer.TrySetResult(e);
            },
            onBridgeEnter: e =>
            {
                // The second channel to enter makes it a two-party bridge; Dial puts the caller in last.
                if (e.UniqueId == callId && e.BridgeNumChannels == "2")
                    bridged.TrySetResult(e);
            }));

        await OriginateTwoPartyBridgeAsync(connection, callId, "blind-transfer-evt-01");

        var bridgeResult = await Task.WhenAny(bridged.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        bridgeResult.Should().Be(bridged.Task, "ext 170 dials ext 171, which answers, so the caller must enter a two-party bridge within 10 s");
        var enter = await bridged.Task;

        var response = await connection.SendActionAsync(new BlindTransferAction
        {
            Channel = enter.Channel,
            Context = "test-functional",
            Exten = "161"
        });
        response.Response.Should().Be("Success", "a BlindTransfer of a party of a two-party Dial bridge must be accepted: {0}", response.Message);

        var transferResult = await Task.WhenAny(blindTransfer.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        transferResult.Should().Be(blindTransfer.Task, "an accepted BlindTransfer must fire a BlindTransfer event naming its transferer within 5 s");

        var transferEvent = await blindTransfer.Task;
        transferEvent.Result.Should().Be("Success", "the transfer of the other party to ext 161 must succeed");
        transferEvent.BridgeUniqueid.Should().Be(enter.BridgeUniqueid, "BlindTransferEvent must name the bridge the transferer was in");
        transferEvent.TransfererChannel.Should().Be(enter.Channel, "BlindTransferEvent must identify the transferer channel");
        transferEvent.Extension.Should().Be("161", "BlindTransferEvent must name the extension the party was sent to");
    }

    /// <summary>
    /// After a RedirectAction, VerbaraServer's ChannelManager should reflect
    /// the channel's new extension context.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23 and 22: after a Redirect to ext 161 that Asterisk reports as a NewExten 161 on the channel, the ChannelManager still holds Extension 100, the value the channel was created with; it sets it from Newchannel and a state reload only")]
    public async Task BlindTransfer_ShouldUpdateChannelManager()
    {
        // The connection reads the dialplan class, so the server receives the NewExten events of the redirect too.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, UseDialplanFlowUser);
        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        var callId = NewCallId("blind-transfer-mgr");
        var redirectSent = 0;
        var reached161 = new TaskCompletionSource<NewExtenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.Subscribe(new TransferEventObserver(
            onNewExten: e =>
            {
                if (e.UniqueId == callId && e.Extension == "161" && Volatile.Read(ref redirectSent) == 1)
                    reached161.TrySetResult(e);
            }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            ChannelId = callId,
            ActionId = "blind-transfer-mgr-01"
        });

        // Wait for the channel to appear in the manager
        await Task.Delay(TimeSpan.FromSeconds(2));

        var targetChannel = server.Channels.GetByUniqueId(callId);
        targetChannel.Should().NotBeNull(
            "originating Local/100@test-functional must put the named channel in the ChannelManager within 2 s");
        targetChannel!.Extension.Should().Be("100", "the channel was created running ext 100");

        Volatile.Write(ref redirectSent, 1);
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = targetChannel.Name,
            Context = "test-functional",
            Exten = "161",
            Priority = 1
        });

        var reached = await Task.WhenAny(reached161.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        reached.Should().Be(reached161.Task, "the redirect must make the channel run ext 161, reported as a NewExten within 5 s");

        // Allow ChannelManager to process the state update
        await Task.Delay(TimeSpan.FromSeconds(1)); // fence-allow: SETTLE — the NewExten has arrived; this lets the server's own subscriber process it

        var updatedChannel = server.Channels.GetByUniqueId(callId);
        updatedChannel.Should().NotBeNull("ext 161 holds the redirected channel for 10 s, so the ChannelManager must still track it");
        server.Channels.GetByName(updatedChannel!.Name).Should().NotBeNull("channel must be in both indices after redirect");
        updatedChannel.Extension.Should().Be("161", "the ChannelManager must reflect the extension the channel was redirected to");
        updatedChannel.Priority.Should().BeGreaterThanOrEqualTo(1, "the channel runs ext 161 from priority 1");
    }

    /// <summary>
    /// Redirecting a channel to a non-existent extension (9999) should result
    /// in the channel hanging up since no dialplan exists.
    /// </summary>
    [Fact]
    public async Task BlindTransfer_ToNonExistentExtension_ShouldFail()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();
        var existingBefore = server.Channels.ActiveChannels.Select(c => c.UniqueId).ToHashSet();

        // Originate a channel to ext 100
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            ActionId = "blind-transfer-fail-01"
        });

        // Wait for the channel to be established
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Pick this test's call: an earlier test's ext 100 call may still be in its Wait and end at any moment.
        var targetChannel = server.Channels.ActiveChannels.FirstOrDefault(c =>
            c.Name.StartsWith("Local/100@test-functional", StringComparison.Ordinal) && !existingBefore.Contains(c.UniqueId));

        targetChannel.Should().NotBeNull(
            "originating Local/100@test-functional must put a Local/ channel in the ChannelManager within 2 s");

        var uniqueId = targetChannel!.UniqueId;

        // Redirect to ext 9999 which does not exist in the dialplan
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = targetChannel.Name,
            Context = "test-functional",
            Exten = "9999",
            Priority = 1
        });

        // Wait for the channel to hang up due to invalid extension
        await Task.Delay(TimeSpan.FromSeconds(4));

        // The functional dialplan defines no 9999 and no 'i' extension, so the redirected channel's PBX
        // ends and Asterisk hangs it up; the ChannelManager must have removed it.
        var afterRedirect = server.Channels.GetByUniqueId(uniqueId);
        afterRedirect.Should().BeNull(
            "a redirect to 9999, which the functional dialplan does not define, must hang the channel up within 4 s");
    }

    /// <summary>
    /// When two channels are bridged via ConfBridge, redirecting one should
    /// transfer only that party while the other remains in the bridge.
    /// </summary>
    [Fact]
    public async Task BlindTransfer_DuringBridge_ShouldTransferOneParty()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();
        var leaveEvents = new ConcurrentBag<BridgeLeaveEvent>();

        using var subscription = connection.Subscribe(new TransferEventObserver(
            onBridgeEnter: enterEvents.Add,
            onBridgeLeave: leaveEvents.Add));

        // Create a bridge with two channels
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-transfer-05",
            IsAsync = true,
            ActionId = "blind-bridge-transfer-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-transfer-05",
            IsAsync = true,
            ActionId = "blind-bridge-transfer-ch2"
        });

        // Wait for both to enter bridge
        await Task.Delay(TimeSpan.FromSeconds(4));

        enterEvents.Should().HaveCountGreaterThanOrEqualTo(2,
            "two ConfBridge originates into test-transfer-05 must produce two BridgeEnter events within 4 s");

        // Redirect only the first channel
        var channelToTransfer = enterEvents.First().Channel;
        channelToTransfer.Should().NotBeNull("a BridgeEnter event must name the channel that entered");

        await connection.SendActionAsync(new RedirectAction
        {
            Channel = channelToTransfer,
            Context = "test-functional",
            Exten = "161",
            Priority = 1
        });

        // Wait for the bridge leave event
        await Task.Delay(TimeSpan.FromSeconds(4));

        // The transferred channel should have left the bridge
        var transferredLeaves = leaveEvents
            .Where(e => e.Channel == channelToTransfer)
            .ToList();

        transferredLeaves.Should().NotBeEmpty(
            "the redirected channel must leave the bridge");
    }

    // ─────────────────────────────────────────────────────────────
    //  Attended Transfer Tests (AtxferAction)
    // ─────────────────────────────────────────────────────────────
    //
    // The AMI Atxfer action plays the atxfer feature code and the target's digits into the channel, so it needs:
    //   1. a channel in a bridge whose features are on for it — the two-party Dial bridge of ext 170 (Dial tT);
    //      a ConfBridge party has no transfer features;
    //   2. an atxfer feature code (features.conf) — without one Asterisk answers "No attended transfer feature
    //      code found";
    //   3. no transfer announcement (features.conf transferannouncesound): the image ships no sound files, and a
    //      missing announcement aborts the transfer before it dials.
    // The transfer completes when the transferer hangs up while talking to the target.

    /// <summary>
    /// AtxferAction on a party of a two-party bridge starts the consultation call to the target extension.
    /// </summary>
    [Fact]
    public async Task AttendedTransfer_ShouldInitiateConsultation()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var events = new ConcurrentQueue<ManagerEvent>();
        using var subscription = connection.Subscribe(new QueueingObserver(events));

        var callId = NewCallId("atxfer-init");
        var (caller, callee) = await BridgeTwoPartiesAsync(connection, events, callId, "atxfer-init-01");

        try
        {
            var sentAt = events.Count;
            var response = await connection.SendActionAsync(new AtxferAction
            {
                Channel = caller.Channel,
                Context = "test-functional",
                Exten = "161",
                Priority = 1
            });
            response.Response.Should().Be("Success", "an Atxfer of a party of a bridge with the transfer features on must be accepted: {0}", response.Message);

            // The consultation is a new call from the transferer to the target extension.
            var consultation = await WaitForAsync<NewChannelEvent>(events, sentAt,
                e => e.Channel?.StartsWith("Local/161@test-functional-", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(5));
            consultation.Should().NotBeNull("the Atxfer must dial the consultation call to ext 161 within 5 s");
        }
        finally
        {
            await HangUpAsync(connection, caller.Channel, callee.Channel);
        }
    }

    /// <summary>
    /// A completed attended transfer fires an AttendedTransferEvent: the transferer hangs up while it talks to the
    /// target, and Asterisk connects the transferee to the target.
    /// </summary>
    [Fact]
    public async Task AttendedTransfer_ShouldFireEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var events = new ConcurrentQueue<ManagerEvent>();
        using var subscription = connection.Subscribe(new QueueingObserver(events));

        var callId = NewCallId("atxfer-event");
        var (caller, callee) = await BridgeTwoPartiesAsync(connection, events, callId, "atxfer-event-01");

        try
        {
            var (evt, _) = await CompleteAttendedTransferAsync(connection, events, caller, "162");

            evt.Result.Should().Be("Success", "the transferer hung up while talking to the target, which completes the transfer");
            evt.OrigTransfererChannel.Should().Be(caller.Channel, "AttendedTransferEvent must identify the original transferer");
            evt.OrigBridgeUniqueid.Should().Be(caller.BridgeUniqueid, "AttendedTransferEvent must name the bridge the transfer started from");
            evt.SecondBridgeUniqueid.Should().NotBeNullOrEmpty("AttendedTransferEvent must name the consultation bridge");
            evt.TransfereeChannel.Should().Be(callee.Channel, "the party the transferer was talking to is the one transferred");

            // Measured on Asterisk 23: the two bridges are merged, so the event names the bridge the transferee and the
            // target end up in (DestType Bridge) and carries no TransferTarget fields.
            evt.DestType.Should().Be("Bridge", "the transfer joins the transferee and the target in one bridge");
            evt.DestBridgeUniqueid.Should().BeOneOf([evt.OrigBridgeUniqueid, evt.SecondBridgeUniqueid],
                "the bridges are merged into one of the two");

            var ping = await connection.SendActionAsync(new PingAction());
            ping.Response.Should().Be("Success", "connection must remain stable after the attended transfer");
        }
        finally
        {
            await HangUpAsync(connection, caller.Channel, callee.Channel);
        }
    }

    /// <summary>
    /// After a completed attended transfer, the BridgeManager shows the transferee and the target in one bridge.
    /// </summary>
    [Fact]
    public async Task AttendedTransfer_ShouldBridgeCallerToTarget()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        var events = new ConcurrentQueue<ManagerEvent>();
        using var subscription = connection.Subscribe(new QueueingObserver(events));

        var callId = NewCallId("atxfer-bridge");
        var (caller, callee) = await BridgeTwoPartiesAsync(connection, events, callId, "atxfer-bridge-01");

        try
        {
            var (evt, targetUniqueId) = await CompleteAttendedTransferAsync(connection, events, caller, "161");
            evt.Result.Should().Be("Success", "the transferer hung up while talking to the target, which completes the transfer");
            evt.TransfereeUniqueid.Should().Be(callee.UniqueId, "the party the transferer was talking to is the one transferred");
            evt.DestBridgeUniqueid.Should().NotBeNullOrEmpty("AttendedTransferEvent must name the bridge the parties end up in");

            // The bridge events that move the parties arrive with the transfer; give the server's subscriber a moment.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            AsteriskBridgeView? transferee = null, target = null;
            while (DateTime.UtcNow < deadline)
            {
                transferee = AsteriskBridgeView.Of(server, evt.TransfereeUniqueid!);
                target = AsteriskBridgeView.Of(server, targetUniqueId);
                if (transferee is not null && transferee == target)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(100)); // fence-allow: GUARD-TIMEOUT — polls the BridgeManager for the observed transfer; a green run leaves as soon as both are in one bridge
            }

            transferee.Should().NotBeNull("the BridgeManager must show the transferee in a bridge after the transfer");
            target.Should().Be(transferee, "the BridgeManager must show the transferee and the target in the same bridge");
            transferee!.BridgeId.Should().Be(evt.DestBridgeUniqueid, "that bridge is the one the AttendedTransfer event names");
        }
        finally
        {
            await HangUpAsync(connection, caller.Channel, callee.Channel);
        }
    }

    /// <summary>
    /// If an attended transfer is cancelled (the consultation call hangs up before completion), the transferer
    /// returns to the original bridge, which the BridgeManager still tracks with both parties.
    /// </summary>
    [Fact]
    public async Task AttendedTransfer_Cancel_ShouldPreserveBridge()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        var events = new ConcurrentQueue<ManagerEvent>();
        using var subscription = connection.Subscribe(new QueueingObserver(events));

        var callId = NewCallId("atxfer-cancel");
        var (caller, callee) = await BridgeTwoPartiesAsync(connection, events, callId, "atxfer-cancel-01");
        var originalBridgeId = caller.BridgeUniqueid;

        try
        {
            var sentAt = events.Count;
            var response = await connection.SendActionAsync(new AtxferAction
            {
                Channel = caller.Channel,
                Context = "test-functional",
                Exten = "163",
                Priority = 1
            });
            response.Response.Should().Be("Success", "an Atxfer of a party of a bridge with the transfer features on must be accepted: {0}", response.Message);

            // ext 163 answers and hangs up 2 s later, which cancels the transfer.
            var consultation = await WaitForAsync<NewChannelEvent>(events, sentAt,
                e => e.Channel?.StartsWith("Local/163@test-functional-", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(5));
            consultation.Should().NotBeNull("the Atxfer must dial the consultation call to ext 163 within 5 s");
            var consultationEnded = await WaitForAsync<HangupEvent>(events, sentAt,
                e => e.UniqueId == consultation!.UniqueId, TimeSpan.FromSeconds(8));
            consultationEnded.Should().NotBeNull("ext 163 hangs up 2 s after it answers, so the consultation must end within 8 s");

            var rejoined = await WaitForAsync<BridgeEnterEvent>(events, sentAt,
                e => e.UniqueId == callId && e.BridgeUniqueid == originalBridgeId, TimeSpan.FromSeconds(5));
            rejoined.Should().NotBeNull("the cancelled transfer must return the transferer to the original bridge within 5 s");

            await Task.Delay(TimeSpan.FromMilliseconds(500)); // fence-allow: SETTLE — the BridgeEnter has arrived; this lets the server's own subscriber process it

            (server.Bridges.GetBridgeForChannel(callId)?.BridgeUniqueid).Should().Be(originalBridgeId,
                "the BridgeManager must show the transferer back in the original bridge");
            (server.Bridges.GetBridgeForChannel(callee.UniqueId!)?.BridgeUniqueid).Should().Be(originalBridgeId,
                "the BridgeManager must show the transferee still in the original bridge");
            events.OfType<AttendedTransferEvent>().Should().NotContain(e => e.OrigTransfererUniqueid == callId && e.Result == "Success",
                "a transfer whose target hung up must not report success");
        }
        finally
        {
            await HangUpAsync(connection, caller.Channel, callee.Channel);
        }
    }

    /// <summary>
    /// The events of a completed attended transfer arrive in the order the transfer happens. Measured on Asterisk 22
    /// and 23: the Atxfer creates the consultation bridge and the target enters it; the transferer leaves the original
    /// bridge and enters the consultation bridge; when the transferer hangs up it leaves the consultation bridge, the
    /// target moves into the original bridge, the AttendedTransfer event follows, and the consultation bridge is
    /// destroyed.
    /// </summary>
    [Fact]
    public async Task AttendedTransfer_Events_ShouldBeChronological()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var events = new ConcurrentQueue<ManagerEvent>();
        using var subscription = connection.Subscribe(new QueueingObserver(events));

        var callId = NewCallId("atxfer-chrono");
        var (caller, callee) = await BridgeTwoPartiesAsync(connection, events, callId, "atxfer-chrono-01");

        try
        {
            var (evt, targetUniqueId) = await CompleteAttendedTransferAsync(connection, events, caller, "162");
            evt.Result.Should().Be("Success", "the transferer hung up while talking to the target, which completes the transfer");
            var origBridge = evt.OrigBridgeUniqueid;
            var secondBridge = evt.SecondBridgeUniqueid;
            origBridge.Should().Be(caller.BridgeUniqueid, "the transfer starts from the bridge the transferer was in");
            secondBridge.Should().NotBeNullOrEmpty("AttendedTransferEvent must name the consultation bridge");

            var destroyed = await WaitForAsync<BridgeDestroyEvent>(events, 0, e => e.BridgeUniqueid == secondBridge, TimeSpan.FromSeconds(3));
            destroyed.Should().NotBeNull("the consultation bridge must be destroyed within 3 s of the transfer");

            // This transfer's events, in the order the observer received them.
            var received = events.ToList();
            string Order() => string.Join(" → ", received.OfType<BridgeEventBase>()
                .Where(e => e is AttendedTransferEvent || e.BridgeUniqueid == origBridge || e.BridgeUniqueid == secondBridge)
                .Select(e => e is AttendedTransferEvent ? "AttendedTransfer" : $"{e.GetType().Name}({e.UniqueId} in {e.BridgeUniqueid})"));
            int IndexOf<T>(Func<T, bool> match) where T : ManagerEvent => received.FindIndex(e => e is T t && match(t));
            int LastIndexOf<T>(Func<T, bool> match) where T : ManagerEvent => received.FindLastIndex(e => e is T t && match(t));

            var secondCreated = IndexOf<BridgeCreateEvent>(e => e.BridgeUniqueid == secondBridge);
            var targetEntersSecond = IndexOf<BridgeEnterEvent>(e => e.UniqueId == targetUniqueId && e.BridgeUniqueid == secondBridge);
            var callerLeavesOrig = IndexOf<BridgeLeaveEvent>(e => e.UniqueId == callId && e.BridgeUniqueid == origBridge);
            var callerEntersSecond = IndexOf<BridgeEnterEvent>(e => e.UniqueId == callId && e.BridgeUniqueid == secondBridge);
            var callerLeavesSecond = IndexOf<BridgeLeaveEvent>(e => e.UniqueId == callId && e.BridgeUniqueid == secondBridge);
            var targetEntersOrig = LastIndexOf<BridgeEnterEvent>(e => e.UniqueId == targetUniqueId && e.BridgeUniqueid == origBridge);
            var transfer = IndexOf<AttendedTransferEvent>(e => e.OrigTransfererUniqueid == callId);
            var secondDestroyed = IndexOf<BridgeDestroyEvent>(e => e.BridgeUniqueid == secondBridge);

            new[] { secondCreated, targetEntersSecond, callerLeavesOrig, callerEntersSecond, callerLeavesSecond, targetEntersOrig, transfer, secondDestroyed }
                .Should().NotContain(-1, "every step of the transfer must have been received; order: {0}", Order());

            secondCreated.Should().BeLessThan(targetEntersSecond, "the consultation bridge is created before the target enters it; order: {0}", Order());
            callerLeavesOrig.Should().BeLessThan(callerEntersSecond, "the transferer leaves the original bridge before it enters the consultation bridge; order: {0}", Order());
            targetEntersSecond.Should().BeLessThan(callerLeavesSecond, "the target is in the consultation bridge before the transferer hangs up; order: {0}", Order());
            callerEntersSecond.Should().BeLessThan(callerLeavesSecond, "the transferer enters the consultation bridge before it leaves it; order: {0}", Order());
            callerLeavesSecond.Should().BeLessThan(targetEntersOrig, "the target moves into the original bridge once the transferer hung up; order: {0}", Order());
            targetEntersOrig.Should().BeLessThan(transfer, "the AttendedTransfer event reports a transfer whose parties are already joined; order: {0}", Order());
            callerLeavesSecond.Should().BeLessThan(secondDestroyed, "the consultation bridge is destroyed after the transferer left it; order: {0}", Order());
            evt.TransfereeUniqueid.Should().Be(callee.UniqueId, "the party the transferer was talking to is the one transferred");
        }
        finally
        {
            await HangUpAsync(connection, caller.Channel, callee.Channel);
        }
    }

    /// <summary>NewExten belongs to the dialplan class, which the default test user does not read.</summary>
    private static void UseDialplanFlowUser(Verbara.Sdk.Ami.Connection.AmiConnectionOptions opts)
    {
        opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
        opts.AutoReconnect = false;
        opts.Username = "dialplanflow";
        opts.Password = "dialplanflowpass";
    }

    /// <summary>A unique id for an originated channel, so a test reads only its own call's events.</summary>
    private static string NewCallId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>
    /// Originates a two-party bridge: <paramref name="callId"/> names the caller, the half of
    /// <c>Local/171@test-functional/n</c> that runs ext 170 and dials ext 171 with the transfer features on (Dial tT).
    /// <c>/n</c> keeps Asterisk from optimizing the Local channels out of the bridge.
    /// </summary>
    private static async Task OriginateTwoPartyBridgeAsync(Verbara.Sdk.Ami.Connection.AmiConnection connection, string callId, string actionId)
    {
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/171@test-functional/n",
            Context = "test-functional",
            Exten = "170",
            Priority = 1,
            IsAsync = true,
            ChannelId = callId,
            ActionId = actionId
        });
    }

    /// <summary>
    /// Originates the two-party bridge of ext 170 and returns the BridgeEnter of each party: the caller, named
    /// <paramref name="callId"/>, and the party it dialled.
    /// </summary>
    private static async Task<(BridgeEnterEvent Caller, BridgeEnterEvent Callee)> BridgeTwoPartiesAsync(
        Verbara.Sdk.Ami.Connection.AmiConnection connection, ConcurrentQueue<ManagerEvent> events, string callId, string actionId)
    {
        await OriginateTwoPartyBridgeAsync(connection, callId, actionId);

        var caller = await WaitForAsync<BridgeEnterEvent>(events, 0, e => e.UniqueId == callId, TimeSpan.FromSeconds(10));
        caller.Should().NotBeNull("ext 170 dials ext 171, which answers, so the caller must enter a bridge within 10 s");
        var callee = await WaitForAsync<BridgeEnterEvent>(events, 0,
            e => e.BridgeUniqueid == caller!.BridgeUniqueid && e.UniqueId != callId, TimeSpan.FromSeconds(5));
        callee.Should().NotBeNull("the party ext 170 dialled must be in the caller's bridge");
        return (caller!, callee!);
    }

    /// <summary>
    /// Starts an attended transfer of <paramref name="caller"/>'s bridge to <paramref name="exten"/>, waits for the
    /// target to answer, hangs the transferer up — which completes the transfer — and returns the AttendedTransfer event.
    /// </summary>
    private static async Task<(AttendedTransferEvent Event, string TargetUniqueId)> CompleteAttendedTransferAsync(
        Verbara.Sdk.Ami.Connection.AmiConnection connection, ConcurrentQueue<ManagerEvent> events, BridgeEnterEvent caller, string exten)
    {
        var sentAt = events.Count;
        var response = await connection.SendActionAsync(new AtxferAction
        {
            Channel = caller.Channel,
            Context = "test-functional",
            Exten = exten,
            Priority = 1
        });
        response.Response.Should().Be("Success", "an Atxfer of a party of a bridge with the transfer features on must be accepted: {0}", response.Message);

        // The consultation call's ;1 half is the target channel: the one the transferer talks to, and the one the
        // transferee is connected to when the transfer completes.
        var targetPrefix = $"Local/{exten}@test-functional-";
        var target = await WaitForAsync<NewChannelEvent>(events, sentAt,
            e => e.Channel?.StartsWith(targetPrefix, StringComparison.Ordinal) == true && e.Channel.EndsWith(";1", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        target.Should().NotBeNull("the Atxfer must dial the consultation call to ext {0} within 5 s", exten);
        var answered = await WaitForAsync<NewStateEvent>(events, sentAt,
            e => e.ChannelStateDesc == "Up" && e.UniqueId == target!.UniqueId, TimeSpan.FromSeconds(5));
        answered.Should().NotBeNull("ext {0} answers the consultation call within 5 s", exten);

        await connection.SendActionAsync(new HangupAction { Channel = caller.Channel!, Cause = 16 });

        var evt = await WaitForAsync<AttendedTransferEvent>(events, sentAt,
            e => e.OrigTransfererUniqueid == caller.UniqueId, TimeSpan.FromSeconds(5));
        evt.Should().NotBeNull("the transferer hanging up while talking to the target must fire an AttendedTransfer event within 5 s");
        return (evt!, target!.UniqueId!);
    }

    /// <summary>The first event of type <typeparamref name="T"/> at or after <paramref name="from"/> that matches, or null at the timeout.</summary>
    private static async Task<T?> WaitForAsync<T>(ConcurrentQueue<ManagerEvent> events, int from, Func<T, bool> match, TimeSpan timeout)
        where T : ManagerEvent
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var found = events.Skip(from).OfType<T>().FirstOrDefault(match);
            if (found is not null || DateTime.UtcNow >= deadline)
                return found;
            await Task.Delay(TimeSpan.FromMilliseconds(50)); // fence-allow: GUARD-TIMEOUT — polls for an observed event; a green run leaves as soon as it arrives
        }
    }

    /// <summary>Hangs up what a test left, ignoring channels that are already gone.</summary>
    private static async Task HangUpAsync(Verbara.Sdk.Ami.Connection.AmiConnection connection, params string?[] channels)
    {
        foreach (var channel in channels.OfType<string>())
            await BestEffort.SendAsync(connection, new HangupAction { Channel = channel, Cause = 16 });
    }

    /// <summary>The bridge the BridgeManager shows a channel in, compared by bridge id.</summary>
    private sealed record AsteriskBridgeView(string BridgeId)
    {
        public static AsteriskBridgeView? Of(VerbaraServer server, string uniqueId) =>
            server.Bridges.GetBridgeForChannel(uniqueId) is { } bridge ? new AsteriskBridgeView(bridge.BridgeUniqueid) : null;
    }

    /// <summary>Observer that routes transfer-related events to callbacks.</summary>
    private sealed class TransferEventObserver(
        Action<BlindTransferEvent>? onBlindTransfer = null,
        Action<AttendedTransferEvent>? onAttendedTransfer = null,
        Action<NewExtenEvent>? onNewExten = null,
        Action<BridgeEnterEvent>? onBridgeEnter = null,
        Action<BridgeLeaveEvent>? onBridgeLeave = null,
        Action<string>? onChannel = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case BlindTransferEvent e: onBlindTransfer?.Invoke(e); break;
                case AttendedTransferEvent e: onAttendedTransfer?.Invoke(e); break;
                case NewExtenEvent e:
                    onNewExten?.Invoke(e);
                    if (e.Channel is not null)
                        onChannel?.Invoke(e.Channel);
                    break;
                case BridgeEnterEvent e: onBridgeEnter?.Invoke(e); break;
                case BridgeLeaveEvent e: onBridgeLeave?.Invoke(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Observer that queues every event in the order it arrives.</summary>
    private sealed class QueueingObserver(ConcurrentQueue<ManagerEvent> queue) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => queue.Enqueue(value);

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
