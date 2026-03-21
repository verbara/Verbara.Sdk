namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Bridge;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tests for bridge lifecycle events (BridgeCreate, BridgeEnter, BridgeLeave, BridgeDestroy)
/// using ConfBridge (ext 600) to create bridges between channels.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BridgeLifecycleTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    public BridgeLifecycleTests() : base("Asterisk.Sdk.Ami")
    {
    }

    /// <summary>
    /// Two channels entering the same ConfBridge should produce a BridgeCreateEvent
    /// and two BridgeEnterEvents sharing the same BridgeUniqueid.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Bridge_ShouldFireCreateAndEnterEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var createEvents = new ConcurrentBag<BridgeCreateEvent>();
        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();

        using var subscription = connection.Subscribe(
            new BridgeEventObserver(onCreate: createEvents.Add, onEnter: enterEvents.Add));

        // Originate two channels into the same ConfBridge
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-01",
            IsAsync = true,
            ActionId = "bridge-create-enter-ch1"
        });

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-01",
            IsAsync = true,
            ActionId = "bridge-create-enter-ch2"
        });

        // Allow time for bridge creation and channel entry
        await Task.Delay(TimeSpan.FromSeconds(5));

        createEvents.Should().NotBeEmpty("ConfBridge must fire at least one BridgeCreateEvent");
        enterEvents.Count.Should().BeGreaterThanOrEqualTo(2,
            "two channels entering ConfBridge must produce at least 2 BridgeEnterEvents");

        // All enter events for the bridge should share the same BridgeUniqueid as the create event
        var bridgeId = createEvents.First().BridgeUniqueid;
        bridgeId.Should().NotBeNullOrEmpty("BridgeCreateEvent must carry a BridgeUniqueid");

        var matchingEnters = enterEvents.Where(e => e.BridgeUniqueid == bridgeId).ToList();
        matchingEnters.Count.Should().BeGreaterThanOrEqualTo(2,
            "both enter events must reference the same bridge");
    }

    /// <summary>
    /// Hanging up a channel in a bridge should fire BridgeLeaveEvent.
    /// When all channels leave, BridgeDestroyEvent should fire.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Bridge_ShouldFireLeaveAndDestroyOnHangup()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var createEvents = new ConcurrentBag<BridgeCreateEvent>();
        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();
        var leaveEvents = new ConcurrentBag<BridgeLeaveEvent>();
        var destroyEvents = new ConcurrentBag<BridgeDestroyEvent>();

        using var subscription = connection.Subscribe(new BridgeEventObserver(
            onCreate: createEvents.Add,
            onEnter: enterEvents.Add,
            onLeave: leaveEvents.Add,
            onDestroy: destroyEvents.Add));

        // Originate two channels into the same ConfBridge
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-02",
            IsAsync = true,
            ActionId = "bridge-leave-destroy-ch1"
        });

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-02",
            IsAsync = true,
            ActionId = "bridge-leave-destroy-ch2"
        });

        // Wait for both channels to enter the bridge
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Hang up all entered channels
        foreach (var enter in enterEvents)
        {
            if (enter.Channel is not null)
            {
                try
                {
                    await connection.SendActionAsync(new HangupAction
                    {
                        Channel = enter.Channel,
                        Cause = 16
                    });
                }
                catch (OperationCanceledException)
                {
                    // Channel may already have hung up
                }
            }
        }

        // Allow time for leave + destroy events
        await Task.Delay(TimeSpan.FromSeconds(4));

        leaveEvents.Should().NotBeEmpty("hanging up bridged channels must fire BridgeLeaveEvent(s)");
        destroyEvents.Should().NotBeEmpty("bridge with no channels must fire BridgeDestroyEvent");
    }

    /// <summary>
    /// BridgeManager on AsteriskServer should track active bridges when channels enter a ConfBridge.
    /// </summary>
    [AsteriskContainerFact]
    public async Task BridgeManager_ShouldTrackActiveBridges()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Originate two channels into a ConfBridge
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-03",
            IsAsync = true,
            ActionId = "bridge-manager-track-ch1"
        });

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-03",
            IsAsync = true,
            ActionId = "bridge-manager-track-ch2"
        });

        // Wait for bridge lifecycle events to propagate
        await Task.Delay(TimeSpan.FromSeconds(5));

        server.Bridges.BridgeCount.Should().BeGreaterThan(0,
            "BridgeManager must track at least one bridge after ConfBridge origination");

        var activeBridges = server.Bridges.ActiveBridges.ToList();
        activeBridges.Should().NotBeEmpty("there should be at least one active bridge");

        // Each active bridge should have channels
        var bridgeWithChannels = activeBridges.FirstOrDefault(b => b.NumChannels > 0);
        bridgeWithChannels.Should().NotBeNull(
            "at least one active bridge should contain channels");
    }

    /// <summary>
    /// Two independent ConfBridges should produce separate bridges with distinct BridgeUniqueids.
    /// </summary>
    [AsteriskContainerFact]
    public async Task MultipleBridges_ShouldBeIndependent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var createEvents = new ConcurrentBag<BridgeCreateEvent>();

        using var subscription = connection.Subscribe(
            new BridgeEventObserver(onCreate: createEvents.Add));

        // First ConfBridge pair
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-04a",
            IsAsync = true,
            ActionId = "bridge-multi-a-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-04a",
            IsAsync = true,
            ActionId = "bridge-multi-a-ch2"
        });

        // Second ConfBridge pair (different conference name)
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-04b",
            IsAsync = true,
            ActionId = "bridge-multi-b-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-04b",
            IsAsync = true,
            ActionId = "bridge-multi-b-ch2"
        });

        // Allow bridges to form
        await Task.Delay(TimeSpan.FromSeconds(5));

        createEvents.Count.Should().BeGreaterThanOrEqualTo(2,
            "two separate ConfBridges must produce at least 2 BridgeCreateEvents");

        var uniqueIds = createEvents.Select(e => e.BridgeUniqueid).Distinct().ToList();
        uniqueIds.Count.Should().BeGreaterThanOrEqualTo(2,
            "each ConfBridge must have a distinct BridgeUniqueid");
    }

    /// <summary>
    /// A ConfBridge with three channels should track all three in enter events.
    /// </summary>
    [AsteriskContainerFact]
    public async Task BridgeWithThreeChannels_ShouldTrackAll()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();
        using var subscription = connection.Subscribe(
            new BridgeEventObserver(onEnter: enterEvents.Add));

        // Originate three channels into the same ConfBridge
        for (var i = 1; i <= 3; i++)
        {
            await connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/600@test-functional",
                Application = "ConfBridge",
                Data = "test-bridge-05",
                IsAsync = true,
                ActionId = $"bridge-three-ch{i}"
            });
        }

        // Allow all three to enter
        await Task.Delay(TimeSpan.FromSeconds(6));

        // Filter enter events for this bridge
        var bridgeIds = enterEvents
            .Select(e => e.BridgeUniqueid)
            .Where(id => id is not null)
            .Distinct()
            .ToList();

        bridgeIds.Should().NotBeEmpty("at least one bridge must have been created");

        // The bridge with the most enters should have 3+
        var maxEnters = bridgeIds
            .Select(id => enterEvents.Count(e => e.BridgeUniqueid == id))
            .Max();

        maxEnters.Should().BeGreaterThanOrEqualTo(3,
            "ConfBridge with 3 channels must produce at least 3 BridgeEnterEvents");

        // Verify BridgeManager tracks the channels
        var activeBridges = server.Bridges.ActiveBridges.ToList();
        var largestBridge = activeBridges.MaxBy(b => b.NumChannels);
        largestBridge.Should().NotBeNull();
        largestBridge!.NumChannels.Should().BeGreaterThanOrEqualTo(3,
            "BridgeManager must track all 3 channels in the bridge");
    }

    /// <summary>
    /// All bridge events (Create, Enter, Leave, Destroy) for the same bridge must share
    /// the same BridgeUniqueid, ensuring consistent correlation.
    /// </summary>
    [AsteriskContainerFact]
    public async Task BridgeEvents_ShouldHaveCorrectBridgeId()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var createEvents = new ConcurrentBag<BridgeCreateEvent>();
        var enterEvents = new ConcurrentBag<BridgeEnterEvent>();
        var leaveEvents = new ConcurrentBag<BridgeLeaveEvent>();
        var destroyEvents = new ConcurrentBag<BridgeDestroyEvent>();

        using var subscription = connection.Subscribe(new BridgeEventObserver(
            onCreate: createEvents.Add,
            onEnter: enterEvents.Add,
            onLeave: leaveEvents.Add,
            onDestroy: destroyEvents.Add));

        // Originate two channels into ConfBridge, then hang them up
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-06",
            IsAsync = true,
            ActionId = "bridge-id-check-ch1"
        });
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = "test-bridge-06",
            IsAsync = true,
            ActionId = "bridge-id-check-ch2"
        });

        // Wait for channels to enter
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Hang up all entered channels to trigger leave + destroy
        foreach (var enter in enterEvents)
        {
            if (enter.Channel is not null)
            {
                try
                {
                    await connection.SendActionAsync(new HangupAction
                    {
                        Channel = enter.Channel,
                        Cause = 16
                    });
                }
                catch (OperationCanceledException)
                {
                    // Channel may already have hung up
                }
            }
        }

        // Wait for leave and destroy events
        await Task.Delay(TimeSpan.FromSeconds(4));

        createEvents.Should().NotBeEmpty("BridgeCreateEvent must fire");
        var bridgeId = createEvents.First().BridgeUniqueid;
        bridgeId.Should().NotBeNullOrEmpty();

        // Every enter event for this bridge must match the create event's BridgeUniqueid
        var relatedEnters = enterEvents.Where(e => e.BridgeUniqueid == bridgeId).ToList();
        relatedEnters.Should().HaveCountGreaterThanOrEqualTo(2,
            "enter events must reference the same BridgeUniqueid as the create event");

        // Leave events must also reference the same BridgeUniqueid
        var relatedLeaves = leaveEvents.Where(e => e.BridgeUniqueid == bridgeId).ToList();
        relatedLeaves.Should().NotBeEmpty(
            "leave events must reference the same BridgeUniqueid");

        // Destroy event must reference the same BridgeUniqueid
        var relatedDestroys = destroyEvents.Where(e => e.BridgeUniqueid == bridgeId).ToList();
        relatedDestroys.Should().NotBeEmpty(
            "destroy event must reference the same BridgeUniqueid");
    }

    /// <summary>Observer that routes bridge-related events to callbacks.</summary>
    private sealed class BridgeEventObserver(
        Action<BridgeCreateEvent>? onCreate = null,
        Action<BridgeEnterEvent>? onEnter = null,
        Action<BridgeLeaveEvent>? onLeave = null,
        Action<BridgeDestroyEvent>? onDestroy = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case BridgeCreateEvent e: onCreate?.Invoke(e); break;
                case BridgeEnterEvent e: onEnter?.Invoke(e); break;
                case BridgeLeaveEvent e: onLeave?.Invoke(e); break;
                case BridgeDestroyEvent e: onDestroy?.Invoke(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
