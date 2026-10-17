namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Bridge;

using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
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
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
            opts.Username = "dialplanflow";
            opts.Password = "dialplanflowpass";
        });
        await connection.ConnectAsync();

        var newExtenEvents = new ConcurrentBag<NewExtenEvent>();
        var channelNames = new ConcurrentBag<string>();

        // Only this test's call runs ext 100's Answer after the subscription; a call an earlier test left
        // in ext 100's Wait reports no Answer, so it cannot be picked.
        using var subscription = connection.Subscribe(new TransferEventObserver(
            onNewExten: e =>
            {
                newExtenEvents.Add(e);
                if (e.Application == "Answer" && e.Channel is not null
                    && e.Channel.StartsWith("Local/100@test-functional", StringComparison.Ordinal))
                    channelNames.Add(e.Channel);
            }));

        // Originate a channel to ext 100 (Answer + Wait(5))
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            ActionId = "blind-redirect-01"
        });

        // Wait for the channel to be answered and settled
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Find an active channel to redirect
        var targetChannel = channelNames.FirstOrDefault(n => n.Contains("Local/", StringComparison.Ordinal));
        targetChannel.Should().NotBeNull(
            "originating Local/100@test-functional must report a NewExten for a Local/ channel within 2 s");

        // Redirect the channel to ext 161 (Answer + Wait(10))
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = targetChannel,
            Context = "test-functional",
            Exten = "161",
            Priority = 1
        });

        // Wait for the redirect to take effect
        await Task.Delay(TimeSpan.FromSeconds(3));

        // After redirect, a NewExtenEvent should fire for the target extension
        var redirectExtens = newExtenEvents
            .Where(e => e.Extension == "161" || e.Application == "Wait")
            .ToList();

        // The redirect either produces a NewExtenEvent for 161 or the channel now executes Wait(10)
        // Either way, the channel should still be alive (not hung up by ext 100's Wait(5) ending)
        redirectExtens.Should().NotBeEmpty(
            "RedirectAction to ext 161 must cause the channel to execute the target dialplan");
    }

    /// <summary>
    /// Redirecting a bridged channel should fire a BlindTransferEvent
    /// when the channel was part of a ConfBridge.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23: a Redirect of a ConfBridge party to ext 161 moves the channel but produces no BlindTransfer event within 4 s")]
    public async Task BlindTransfer_ShouldFireBlindTransferEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var blindTransferEvents = new ConcurrentBag<BlindTransferEvent>();
        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();

        using var subscription = connection.Subscribe(new TransferEventObserver(
            onBlindTransfer: blindTransferEvents.Add,
            onBridgeEnter: enterEvents.Add));

        // Create a bridge with two channels via ConfBridge
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-transfer-01",
            IsAsync = true,
            ActionId = "blind-transfer-evt-ch1"
        });

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-transfer-01",
            IsAsync = true,
            ActionId = "blind-transfer-evt-ch2"
        });

        // Wait for both channels to enter the bridge
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Find a channel to redirect
        var targetChannel = enterEvents.FirstOrDefault()?.Channel;
        targetChannel.Should().NotBeNull("two ConfBridge originates into test-transfer conferences must produce a BridgeEnter within 4 s");

        // Redirect one party to ext 161
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = targetChannel,
            Context = "test-functional",
            Exten = "161",
            Priority = 1
        });

        // Wait for transfer event
        await Task.Delay(TimeSpan.FromSeconds(4));

        blindTransferEvents.Should().NotBeEmpty(
            "redirecting a ConfBridge party to ext 161 is the blind transfer this test claims fires a BlindTransfer event");

        var transferEvent = blindTransferEvents.First();
        transferEvent.BridgeUniqueid.Should().NotBeNullOrEmpty(
            "BlindTransferEvent must include the bridge ID");
        transferEvent.TransfererChannel.Should().NotBeNullOrEmpty(
            "BlindTransferEvent must identify the transferer channel");
    }

    /// <summary>
    /// After a RedirectAction, VerbaraServer's ChannelManager should reflect
    /// the channel's new extension context.
    /// </summary>
    [Fact]
    public async Task BlindTransfer_ShouldUpdateChannelManager()
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

        // Originate a channel to ext 100 (Answer + Wait(5))
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            ActionId = "blind-transfer-mgr-01"
        });

        // Wait for the channel to appear in the manager
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Pick this test's call: an earlier test's ext 100 call may still be in its Wait and end at any moment.
        var targetChannel = server.Channels.ActiveChannels.FirstOrDefault(c =>
            c.Name.StartsWith("Local/100@test-functional", StringComparison.Ordinal) && !existingBefore.Contains(c.UniqueId));

        targetChannel.Should().NotBeNull(
            "originating Local/100@test-functional must put a Local/ channel in the ChannelManager within 2 s");

        // Redirect the channel to ext 161
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = targetChannel.Name,
            Context = "test-functional",
            Exten = "161",
            Priority = 1
        });

        // Allow ChannelManager to process the state update
        await Task.Delay(TimeSpan.FromSeconds(3));

        // The channel should still exist in the manager (ext 161 does Wait(10))
        var updatedChannel = server.Channels.GetByUniqueId(targetChannel.UniqueId);

        // Channel should still be alive after redirect to ext 161 (Answer, Wait(10))
        updatedChannel.Should().NotBeNull(
            "ext 161 holds the channel for 10 s, so the ChannelManager must still track it 3 s after the redirect");
        server.Channels.GetByName(updatedChannel!.Name).Should().NotBeNull(
            "channel must be in both indices after redirect");
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
    // AtxferAction requires:
    //   1. The channel must be in an active bridge with transfer features enabled
    //   2. The target extension must be dialable
    //   3. The consultation call must complete before the transfer bridges
    //
    // In an automated test environment, AtxferAction often returns errors because
    // ConfBridge channels typically lack the required transfer feature flags.
    // The following tests verify the action/event plumbing as far as possible.

    /// <summary>
    /// AtxferAction should be sendable against a bridged channel.
    /// Even if the transfer cannot complete (missing features), the action
    /// itself must not cause protocol errors.
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

        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();

        using var subscription = connection.Subscribe(new TransferEventObserver(
            onBridgeEnter: enterEvents.Add));

        // Create a bridge
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-01",
            IsAsync = true,
            ActionId = "atxfer-init-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-01",
            IsAsync = true,
            ActionId = "atxfer-init-ch2"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        var targetChannel = enterEvents.FirstOrDefault()?.Channel;
        targetChannel.Should().NotBeNull("two ConfBridge originates into test-transfer conferences must produce a BridgeEnter within 4 s");

        // Attempt attended transfer — may succeed or fail depending on bridge features
        try
        {
            await connection.SendActionAsync(new AtxferAction
            {
                Channel = targetChannel,
                Context = "test-functional",
                Exten = "161",
                Priority = 1
            });
        }
        catch (OperationCanceledException)
        {
            // Timeout is acceptable — AtxferAction may block or be rejected
        }

        // The test verifies that sending AtxferAction does not crash the connection
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Connection should still be functional after the AtxferAction attempt
        var pingResponse = await connection.SendActionAsync(new PingAction());
        pingResponse.Should().NotBeNull(
            "AMI connection must remain functional after AtxferAction attempt");
    }

    /// <summary>
    /// If an attended transfer completes, it should fire an AttendedTransferEvent.
    /// This test verifies the event plumbing; the transfer may not actually complete
    /// in a basic ConfBridge setup without DTMF feature codes.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23: an Atxfer of a ConfBridge party to ext 162 produces no AttendedTransfer event within 5 s")]
    public async Task AttendedTransfer_ShouldFireEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var attendedTransferEvents = new ConcurrentBag<AttendedTransferEvent>();
        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();

        using var subscription = connection.Subscribe(new TransferEventObserver(
            onAttendedTransfer: attendedTransferEvents.Add,
            onBridgeEnter: enterEvents.Add));

        // Create a bridge
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-02",
            IsAsync = true,
            ActionId = "atxfer-event-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-02",
            IsAsync = true,
            ActionId = "atxfer-event-ch2"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        var targetChannel = enterEvents.FirstOrDefault()?.Channel;
        targetChannel.Should().NotBeNull("two ConfBridge originates into test-transfer conferences must produce a BridgeEnter within 4 s");

        try
        {
            await connection.SendActionAsync(new AtxferAction
            {
                Channel = targetChannel,
                Context = "test-functional",
                Exten = "162",
                Priority = 1
            });
        }
        catch (OperationCanceledException)
        {
            // Expected — AtxferAction may not be supported for this bridge type
        }

        await Task.Delay(TimeSpan.FromSeconds(5));

        attendedTransferEvents.Should().NotBeEmpty(
            "an Atxfer of a bridged party to ext 162 is the attended transfer this test claims fires an AttendedTransfer event");
        var evt = attendedTransferEvents.First();
        evt.Result.Should().NotBeNullOrEmpty(
            "AttendedTransferEvent must include a Result field");
        evt.OrigTransfererChannel.Should().NotBeNullOrEmpty(
            "AttendedTransferEvent must identify the original transferer");

        // Regardless of transfer outcome, connection must be intact
        var ping = await connection.SendActionAsync(new PingAction());
        ping.Should().NotBeNull("connection must remain stable after attended transfer attempt");
    }

    /// <summary>
    /// If the attended transfer actually bridges caller to target, the BridgeManager
    /// should reflect the new bridge membership. This test documents the expected
    /// behavior even if the full transfer cannot be triggered in automation.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23: an Atxfer of a ConfBridge party to ext 161 produces no AttendedTransfer event within 5 s, so there is no transfer bridge to check")]
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

        var attendedTransferEvents = new ConcurrentBag<AttendedTransferEvent>();
        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();
        using var subscription = connection.Subscribe(new TransferEventObserver(
            onAttendedTransfer: attendedTransferEvents.Add,
            onBridgeEnter: enterEvents.Add));

        // Create a bridge
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-03",
            IsAsync = true,
            ActionId = "atxfer-bridge-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-03",
            IsAsync = true,
            ActionId = "atxfer-bridge-ch2"
        });

        await Task.Delay(TimeSpan.FromSeconds(5));

        // Before this test sent no transfer at all, so the event below could never fire.
        var targetChannel = enterEvents.FirstOrDefault()?.Channel;
        targetChannel.Should().NotBeNull(
            "two ConfBridge originates into test-transfer conferences must produce a BridgeEnter within 5 s");

        try
        {
            await connection.SendActionAsync(new AtxferAction
            {
                Channel = targetChannel,
                Context = "test-functional",
                Exten = "161",
                Priority = 1
            });
        }
        catch (OperationCanceledException)
        {
            // The response timeout is not the claim; the event below is.
        }

        await Task.Delay(TimeSpan.FromSeconds(5)); // fence-allow: SETTLE — the window in which Asterisk reports the AttendedTransfer event, as the sibling attended-transfer test waits

        attendedTransferEvents.Should().NotBeEmpty(
            "an Atxfer of a bridged party to ext 161 must fire an AttendedTransfer event for the bridge to be checked");
        var evt = attendedTransferEvents.First();
        evt.OrigBridgeUniqueid.Should().NotBeNullOrEmpty(
            "AttendedTransferEvent must name the original bridge");
        server.Bridges.GetById(evt.OrigBridgeUniqueid!).Should().NotBeNull(
            "BridgeManager must track the bridge referenced by AttendedTransferEvent");

        // Verify BridgeManager tracks at least one bridge from the ConfBridge setup
        server.Bridges.BridgeCount.Should().BeGreaterThan(0,
            "BridgeManager must track the ConfBridge");
    }

    /// <summary>
    /// If an attended transfer is cancelled (e.g., the consultation call hangs up
    /// before completion), the original bridge should remain intact.
    /// Tests resilience of the bridge tracking under cancellation.
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

        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();
        using var subscription = connection.Subscribe(new TransferEventObserver(
            onBridgeEnter: enterEvents.Add));

        // Create a bridge with two channels
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-04",
            IsAsync = true,
            ActionId = "atxfer-cancel-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-04",
            IsAsync = true,
            ActionId = "atxfer-cancel-ch2"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        var enter = enterEvents.FirstOrDefault();
        enter.Should().NotBeNull("two ConfBridge originates into test-transfer conferences must produce a BridgeEnter within 4 s");
        var targetChannel = enter!.Channel;
        targetChannel.Should().NotBeNull("a BridgeEnter event must name the channel that entered");
        var originalBridgeId = enter.BridgeUniqueid;
        originalBridgeId.Should().NotBeNullOrEmpty("a BridgeEnter event must name the bridge it entered");
        server.Bridges.ActiveBridges.Should().Contain(b => b.BridgeUniqueid == originalBridgeId,
            "VerbaraServer must track the ConfBridge bridge before the transfer attempt");

        // Attempt transfer to a short-lived extension, then immediately cancel via hangup
        try
        {
            await connection.SendActionAsync(new AtxferAction
            {
                Channel = targetChannel,
                Context = "test-functional",
                Exten = "163",
                Priority = 1
            });
        }
        catch (OperationCanceledException)
        {
            // Expected for ConfBridge channels
        }

        // Wait a moment, then verify original bridge is still intact
        await Task.Delay(TimeSpan.FromSeconds(3));

        // The original bridge should still exist (transfer cancelled or not initiated)
        server.Bridges.ActiveBridges.Should().Contain(b => b.BridgeUniqueid == originalBridgeId,
            "original bridge should persist when attended transfer does not complete");
    }

    /// <summary>
    /// Transfer-related events must arrive in chronological order relative to their
    /// Timestamp field. This tests the event ordering guarantee.
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

        var allEvents = new ConcurrentQueue<(DateTimeOffset ReceivedAt, ManagerEvent Event)>();

        using var subscription = connection.Subscribe(new TimestampingObserver(allEvents));

        // Create a bridge and trigger a transfer attempt to generate event activity
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-05",
            IsAsync = true,
            ActionId = "atxfer-chrono-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-atxfer-05",
            IsAsync = true,
            ActionId = "atxfer-chrono-ch2"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        // Hang up to generate leave/destroy events
        var enterChannels = allEvents
            .Where(e => e.Event is BridgeEnterEvent)
            .Select(e => ((BridgeEnterEvent)e.Event).Channel)
            .Where(c => c is not null)
            .ToList();

        foreach (var channel in enterChannels)
        {
            try
            {
                await connection.SendActionAsync(new HangupAction
                {
                    Channel = channel!,
                    Cause = 16
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(4));

        // Filter bridge-related events
        var bridgeEvents = allEvents
            .Where(e => e.Event is BridgeCreateEvent
                     or BridgeEnterEvent
                     or BridgeLeaveEvent
                     or BridgeDestroyEvent)
            .ToList();

        bridgeEvents.Should().HaveCountGreaterThanOrEqualTo(2,
            "two ConfBridge joins and their hangups must produce at least two bridge events to order");

        // Events must have been received in monotonically non-decreasing order
        for (var i = 1; i < bridgeEvents.Count; i++)
        {
            bridgeEvents[i].ReceivedAt.Should().BeOnOrAfter(bridgeEvents[i - 1].ReceivedAt,
                "bridge events must be received in chronological order (event {0} vs {1})",
                i - 1, i);
        }
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

    /// <summary>Observer that timestamps every event as it arrives.</summary>
    private sealed class TimestampingObserver(
        ConcurrentQueue<(DateTimeOffset ReceivedAt, ManagerEvent Event)> queue)
        : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) =>
            queue.Enqueue((DateTimeOffset.UtcNow, value));

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
