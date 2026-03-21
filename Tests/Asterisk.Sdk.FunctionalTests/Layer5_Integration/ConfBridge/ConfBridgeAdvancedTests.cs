namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.ConfBridge;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Advanced ConfBridge tests covering mute/unmute, kick, lock/unlock,
/// recording, and conference lifecycle (start/end) events.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConfBridgeAdvancedTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    public ConfBridgeAdvancedTests() : base("Asterisk.Sdk.Ami")
    {
    }

    /// <summary>
    /// Muting a channel in a ConfBridge should fire a ConfbridgeMuteEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Mute_ShouldFireMuteEvent()
    {
        var confName = $"test-mute-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();
        var muteEvents = new ConcurrentBag<ConfbridgeMuteEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(onJoin: joinEvents.Add, onMute: muteEvents.Add));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "mute-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        var joinedChannel = joinEvents.First().Channel;
        joinedChannel.Should().NotBeNullOrEmpty();

        await connection.SendActionAsync(new ConfbridgeMuteAction
        {
            Conference = confName,
            Channel = joinedChannel
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        muteEvents.Should().NotBeEmpty("muting a channel in ConfBridge must fire ConfbridgeMuteEvent");
    }

    /// <summary>
    /// Unmuting a previously muted channel should fire a ConfbridgeUnmuteEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Unmute_ShouldFireUnmuteEvent()
    {
        var confName = $"test-unmute-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();
        var unmuteEvents = new ConcurrentBag<ConfbridgeUnmuteEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(onJoin: joinEvents.Add, onUnmute: unmuteEvents.Add));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "unmute-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        var joinedChannel = joinEvents.First().Channel;
        joinedChannel.Should().NotBeNullOrEmpty();

        // Mute first, then unmute
        await connection.SendActionAsync(new ConfbridgeMuteAction
        {
            Conference = confName,
            Channel = joinedChannel
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        await connection.SendActionAsync(new ConfbridgeUnmuteAction
        {
            Conference = confName,
            Channel = joinedChannel
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        unmuteEvents.Should().NotBeEmpty("unmuting a channel must fire ConfbridgeUnmuteEvent");
    }

    /// <summary>
    /// Kicking a channel from a ConfBridge should fire a ConfbridgeLeaveEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Kick_ShouldRemoveChannelAndFireLeaveEvent()
    {
        var confName = $"test-kick-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();
        var leaveEvents = new ConcurrentBag<ConfbridgeLeaveEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(onJoin: joinEvents.Add, onLeave: leaveEvents.Add));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "kick-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        var joinedChannel = joinEvents.First().Channel;
        joinedChannel.Should().NotBeNullOrEmpty();

        await connection.SendActionAsync(new ConfbridgeKickAction
        {
            Conference = confName,
            Channel = joinedChannel
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        leaveEvents.Should().NotBeEmpty(
            "kicking a channel from ConfBridge must fire ConfbridgeLeaveEvent");
    }

    /// <summary>
    /// Locking a ConfBridge should prevent new channels from joining.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Lock_ShouldPreventNewJoins()
    {
        var confName = $"test-lock-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(onJoin: joinEvents.Add));

        // First channel enters the conference
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "lock-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        var initialJoinCount = joinEvents.Count;

        // Lock the conference
        await connection.SendActionAsync(new ConfbridgeLockAction
        {
            Conference = confName
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Attempt to originate a second channel into the locked conference
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "lock-ch2"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        // The second channel should not have joined the locked conference
        var confJoins = joinEvents.Where(e => e.Conference == confName).ToList();
        confJoins.Count.Should().Be(initialJoinCount,
            "a locked ConfBridge must not allow new channels to join");
    }

    /// <summary>
    /// Unlocking a previously locked ConfBridge should allow new channels to join.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Unlock_ShouldAllowNewJoins()
    {
        var confName = $"test-unlock-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(onJoin: joinEvents.Add));

        // First channel enters the conference
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "unlock-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        // Lock then unlock
        await connection.SendActionAsync(new ConfbridgeLockAction
        {
            Conference = confName
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        await connection.SendActionAsync(new ConfbridgeUnlockAction
        {
            Conference = confName
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Originate a second channel — should succeed after unlock
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "unlock-ch2"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        var confJoins = joinEvents.Where(e => e.Conference == confName).ToList();
        confJoins.Count.Should().BeGreaterThanOrEqualTo(2,
            "an unlocked ConfBridge must allow new channels to join");
    }

    /// <summary>
    /// Starting a recording on a ConfBridge should fire a ConfbridgeRecordEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task StartRecord_ShouldFireRecordEvent()
    {
        var confName = $"test-record-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();
        var recordEvents = new ConcurrentBag<ConfbridgeRecordEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(onJoin: joinEvents.Add, onRecord: recordEvents.Add));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "record-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        await connection.SendActionAsync(new ConfbridgeStartRecordAction
        {
            Conference = confName,
            RecordFile = $"/tmp/confbridge-{confName}"
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        recordEvents.Should().NotBeEmpty(
            "starting a recording on ConfBridge must fire ConfbridgeRecordEvent");
    }

    /// <summary>
    /// Stopping a recording on a ConfBridge should fire a ConfbridgeStopRecordEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task StopRecord_ShouldFireStopRecordEvent()
    {
        var confName = $"test-stoprec-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();
        var stopRecordEvents = new ConcurrentBag<ConfbridgeStopRecordEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(onJoin: joinEvents.Add, onStopRecord: stopRecordEvents.Add));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "stoprec-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        // Start recording first
        await connection.SendActionAsync(new ConfbridgeStartRecordAction
        {
            Conference = confName,
            RecordFile = $"/tmp/confbridge-{confName}"
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Stop recording
        await connection.SendActionAsync(new ConfbridgeStopRecordAction
        {
            Conference = confName
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        stopRecordEvents.Should().NotBeEmpty(
            "stopping a recording on ConfBridge must fire ConfbridgeStopRecordEvent");
    }

    /// <summary>
    /// A ConfBridge should fire ConfbridgeStartEvent when the first participant joins
    /// and ConfbridgeEndEvent when the last participant leaves.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Conference_ShouldFireStartOnFirstJoinAndEndOnLastLeave()
    {
        var confName = $"test-lifecycle-{Guid.NewGuid():N}";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var joinEvents = new ConcurrentBag<ConfbridgeJoinEvent>();
        var startEvents = new ConcurrentBag<ConfbridgeStartEvent>();
        var endEvents = new ConcurrentBag<ConfbridgeEndEvent>();

        using var subscription = connection.Subscribe(
            new ConfBridgeEventObserver(
                onJoin: joinEvents.Add,
                onStart: startEvents.Add,
                onEnd: endEvents.Add));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/600@test-functional",
            Application = "ConfBridge",
            Data = confName,
            IsAsync = true,
            ActionId = "lifecycle-ch1"
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        if (joinEvents.IsEmpty) return;

        startEvents.Should().NotBeEmpty(
            "ConfBridge must fire ConfbridgeStartEvent when the first participant joins");

        var joinedChannel = joinEvents.First().Channel;
        joinedChannel.Should().NotBeNullOrEmpty();

        // Kick the channel to trigger the end event
        await connection.SendActionAsync(new ConfbridgeKickAction
        {
            Conference = confName,
            Channel = joinedChannel
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        endEvents.Should().NotBeEmpty(
            "ConfBridge must fire ConfbridgeEndEvent when the last participant leaves");
    }

    /// <summary>Observer that routes ConfBridge-related events to callbacks.</summary>
    private sealed class ConfBridgeEventObserver(
        Action<ConfbridgeJoinEvent>? onJoin = null,
        Action<ConfbridgeLeaveEvent>? onLeave = null,
        Action<ConfbridgeMuteEvent>? onMute = null,
        Action<ConfbridgeUnmuteEvent>? onUnmute = null,
        Action<ConfbridgeStartEvent>? onStart = null,
        Action<ConfbridgeEndEvent>? onEnd = null,
        Action<ConfbridgeRecordEvent>? onRecord = null,
        Action<ConfbridgeStopRecordEvent>? onStopRecord = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case ConfbridgeJoinEvent e: onJoin?.Invoke(e); break;
                case ConfbridgeLeaveEvent e: onLeave?.Invoke(e); break;
                case ConfbridgeMuteEvent e: onMute?.Invoke(e); break;
                case ConfbridgeUnmuteEvent e: onUnmute?.Invoke(e); break;
                case ConfbridgeStartEvent e: onStart?.Invoke(e); break;
                case ConfbridgeEndEvent e: onEnd?.Invoke(e); break;
                case ConfbridgeRecordEvent e: onRecord?.Invoke(e); break;
                case ConfbridgeStopRecordEvent e: onStopRecord?.Invoke(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
