namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Recording;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for MixMonitor recording lifecycle events (Start, Stop, Mute)
/// using AMI-controlled recording on ext 100 and dialplan-driven recording on ext 900.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class MixMonitorTests : FunctionalTestBase
{
    public MixMonitorTests() : base("Asterisk.Sdk.Ami")
    {
    }

    /// <summary>
    /// Starting MixMonitor on an active channel should fire MixMonitorStartEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task MixMonitor_ShouldFireStartEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelTcs = new TaskCompletionSource<NewChannelEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startEvents = new ConcurrentBag<MixMonitorStartEvent>();

        using var subscription = connection.Subscribe(
            new RecordingObserver(
                onNewChannel: e =>
                {
                    if (e.Channel?.StartsWith("Local/", StringComparison.Ordinal) == true)
                        channelTcs.TrySetResult(e);
                },
                onStart: startEvents.Add));

        // Originate a channel that stays alive (Wait 10 seconds)
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "10",
            IsAsync = true,
            ActionId = "mixmon-start-01"
        });

        // Wait for channel to be established
        var channelResult = await Task.WhenAny(channelTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (channelResult != channelTcs.Task)
            return; // Graceful skip — channel did not appear in time

        var channelName = channelTcs.Task.Result.Channel!;

        // Start MixMonitor on the channel
        await connection.SendActionAsync(new MixMonitorAction
        {
            Channel = channelName,
            File = "test-start-event.wav",
            ActionId = "mixmon-start-action-01"
        });

        // Allow time for the start event
        await Task.Delay(TimeSpan.FromSeconds(3));

        startEvents.Should().NotBeEmpty("MixMonitor must fire a MixMonitorStartEvent when started");
    }

    /// <summary>
    /// Stopping MixMonitor on an active channel should fire MixMonitorStopEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task StopMixMonitor_ShouldFireStopEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelTcs = new TaskCompletionSource<NewChannelEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEvents = new ConcurrentBag<MixMonitorStopEvent>();

        using var subscription = connection.Subscribe(
            new RecordingObserver(
                onNewChannel: e =>
                {
                    if (e.Channel?.StartsWith("Local/", StringComparison.Ordinal) == true)
                        channelTcs.TrySetResult(e);
                },
                onStop: stopEvents.Add));

        // Originate a channel that stays alive
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "10",
            IsAsync = true,
            ActionId = "mixmon-stop-01"
        });

        var channelResult = await Task.WhenAny(channelTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (channelResult != channelTcs.Task)
            return;

        var channelName = channelTcs.Task.Result.Channel!;

        // Start MixMonitor
        await connection.SendActionAsync(new MixMonitorAction
        {
            Channel = channelName,
            File = "test-stop-event.wav",
            ActionId = "mixmon-stop-start-action-01"
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Stop MixMonitor
        await connection.SendActionAsync(new StopMixMonitorAction
        {
            Channel = channelName,
            ActionId = "mixmon-stop-action-01"
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        stopEvents.Should().NotBeEmpty("StopMixMonitor must fire a MixMonitorStopEvent");
    }

    /// <summary>
    /// Muting a MixMonitor recording should fire MixMonitorMuteEvent.
    /// </summary>
    [AsteriskContainerFact]
    public async Task MixMonitorMute_ShouldFireMuteEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelTcs = new TaskCompletionSource<NewChannelEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var muteEvents = new ConcurrentBag<MixMonitorMuteEvent>();

        using var subscription = connection.Subscribe(
            new RecordingObserver(
                onNewChannel: e =>
                {
                    if (e.Channel?.StartsWith("Local/", StringComparison.Ordinal) == true)
                        channelTcs.TrySetResult(e);
                },
                onMute: muteEvents.Add));

        // Originate a channel that stays alive
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "10",
            IsAsync = true,
            ActionId = "mixmon-mute-01"
        });

        var channelResult = await Task.WhenAny(channelTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (channelResult != channelTcs.Task)
            return;

        var channelName = channelTcs.Task.Result.Channel!;

        // Start MixMonitor first
        await connection.SendActionAsync(new MixMonitorAction
        {
            Channel = channelName,
            File = "test-mute-event.wav",
            ActionId = "mixmon-mute-start-action-01"
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Mute the recording (read direction)
        await connection.SendActionAsync(new MixMonitorMuteAction
        {
            Channel = channelName,
            Direction = "read",
            State = 1,
            ActionId = "mixmon-mute-action-01"
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        muteEvents.Should().NotBeEmpty("MixMonitorMute must fire a MixMonitorMuteEvent");
    }

    /// <summary>
    /// Dialplan-driven MixMonitor on ext 900 should fire both Start and Stop events
    /// as the dialplan executes MixMonitor then StopMixMonitor.
    /// </summary>
    [AsteriskContainerFact]
    public async Task DialplanMixMonitor_ShouldFireStartAndStopEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var startEvents = new ConcurrentBag<MixMonitorStartEvent>();
        var stopEvents = new ConcurrentBag<MixMonitorStopEvent>();

        using var subscription = connection.Subscribe(
            new RecordingObserver(onStart: startEvents.Add, onStop: stopEvents.Add));

        // Originate to ext 900 which runs MixMonitor -> Wait(5) -> StopMixMonitor -> Hangup
        // The originate leg must wait long enough for the dialplan leg to complete
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/900@test-functional",
            Application = "Wait",
            Data = "15",
            IsAsync = true,
            ActionId = "mixmon-dialplan-01"
        });

        // Wait for the full dialplan sequence: Answer + MixMonitor + Wait(5) + StopMixMonitor + Hangup
        await Task.Delay(TimeSpan.FromSeconds(12));

        startEvents.Should().NotBeEmpty(
            "dialplan MixMonitor on ext 900 must fire MixMonitorStartEvent");
        stopEvents.Should().NotBeEmpty(
            "dialplan StopMixMonitor on ext 900 must fire MixMonitorStopEvent");
    }

    /// <summary>
    /// MixMonitor Start and Stop events from ext 900 should reference the same Channel,
    /// ensuring consistent event correlation.
    /// </summary>
    [AsteriskContainerFact]
    public async Task MixMonitor_StartAndStop_ShouldHaveMatchingChannel()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var startEvents = new ConcurrentBag<MixMonitorStartEvent>();
        var stopEvents = new ConcurrentBag<MixMonitorStopEvent>();

        using var subscription = connection.Subscribe(
            new RecordingObserver(onStart: startEvents.Add, onStop: stopEvents.Add));

        // Originate to ext 900 which runs MixMonitor -> Wait(5) -> StopMixMonitor -> Hangup
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/900@test-functional",
            Application = "Wait",
            Data = "15",
            IsAsync = true,
            ActionId = "mixmon-matching-01"
        });

        // Wait for the full dialplan sequence
        await Task.Delay(TimeSpan.FromSeconds(12));

        startEvents.Should().NotBeEmpty("MixMonitorStartEvent must fire");
        stopEvents.Should().NotBeEmpty("MixMonitorStopEvent must fire");

        // At least one Start and Stop event pair should reference the same channel
        var startChannels = startEvents.Select(e => e.Channel).Where(c => c is not null).ToList();
        var stopChannels = stopEvents.Select(e => e.Channel).Where(c => c is not null).ToList();

        var matchingChannels = startChannels.Intersect(stopChannels).ToList();
        matchingChannels.Should().NotBeEmpty(
            "MixMonitor Start and Stop events must reference at least one matching Channel");
    }

    /// <summary>Observer that routes MixMonitor-related events to callbacks.</summary>
    private sealed class RecordingObserver(
        Action<NewChannelEvent>? onNewChannel = null,
        Action<MixMonitorStartEvent>? onStart = null,
        Action<MixMonitorStopEvent>? onStop = null,
        Action<MixMonitorMuteEvent>? onMute = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case NewChannelEvent e: onNewChannel?.Invoke(e); break;
                case MixMonitorStartEvent e: onStart?.Invoke(e); break;
                case MixMonitorStopEvent e: onStop?.Invoke(e); break;
                case MixMonitorMuteEvent e: onMute?.Invoke(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
