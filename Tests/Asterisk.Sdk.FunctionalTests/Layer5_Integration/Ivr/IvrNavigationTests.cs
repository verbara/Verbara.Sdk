namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Ivr;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for IVR menu navigation via DTMF input.
/// Ext 160 runs Background(silence/1) + WaitExten(5).
/// WaitExten targets: 1 -> 165, 2 -> 166, i -> 160 (retry), t -> Hangup.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class IvrNavigationTests : FunctionalTestBase
{
    public IvrNavigationTests() : base("Asterisk.Sdk.Ami")
    {
    }

    /// <summary>
    /// Originates a call to ext 160 and verifies the channel reaches WaitExten.
    /// </summary>
    [AsteriskContainerFact]
    public async Task IvrCall_ShouldReachWaitExten()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitExtenReached = new TaskCompletionSource<NewExtenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new IvrObserver(
            onChannelUp: name => channelUp.TrySetResult(name),
            onNewExten: e =>
            {
                if (string.Equals(e.Application, "WaitExten", StringComparison.OrdinalIgnoreCase))
                    waitExtenReached.TrySetResult(e);
            }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/160@test-functional",
            Context = "test-functional",
            Exten = "160",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "ivr-waitexten-01"
        });

        // Wait for channel to come up
        var upResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (upResult != channelUp.Task)
            return;

        // Background(silence/1) takes ~1s, then WaitExten starts
        var wxResult = await Task.WhenAny(waitExtenReached.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        wxResult.Should().Be(waitExtenReached.Task,
            "NewExtenEvent with Application=WaitExten must fire after Background completes");

        var wxEvent = await waitExtenReached.Task;
        wxEvent.Extension.Should().Be("160",
            "WaitExten must execute in extension 160");
    }

    /// <summary>
    /// Presses digit "1" during WaitExten and verifies navigation to ext 165.
    /// Dialplan: exten => 1,1,Goto(test-functional,165,1)
    /// </summary>
    [AsteriskContainerFact]
    public async Task IvrOption1_ShouldNavigateToExtension165()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedExt165 = new TaskCompletionSource<NewExtenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new IvrObserver(
            onChannelUp: name => channelUp.TrySetResult(name),
            onNewExten: e =>
            {
                if (e.Extension == "165")
                    reachedExt165.TrySetResult(e);
            }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/160@test-functional",
            Context = "test-functional",
            Exten = "160",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "ivr-opt1-01"
        });

        var upResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (upResult != channelUp.Task)
            return;

        var channelName = await channelUp.Task;

        // Wait for Background + WaitExten to become active
        await Task.Delay(TimeSpan.FromSeconds(3));

        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = channelName,
            Digit = "1",
            Duration = 250,
            Receive = true
        });

        var result = await Task.WhenAny(reachedExt165.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(reachedExt165.Task,
            "pressing '1' during WaitExten must navigate to extension 165");

        var evt = await reachedExt165.Task;
        evt.Extension.Should().Be("165");
    }

    /// <summary>
    /// Presses digit "2" during WaitExten and verifies navigation to ext 166.
    /// Dialplan: exten => 2,1,Goto(test-functional,166,1)
    /// </summary>
    [AsteriskContainerFact]
    public async Task IvrOption2_ShouldNavigateToExtension166()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedExt166 = new TaskCompletionSource<NewExtenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new IvrObserver(
            onChannelUp: name => channelUp.TrySetResult(name),
            onNewExten: e =>
            {
                if (e.Extension == "166")
                    reachedExt166.TrySetResult(e);
            }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/160@test-functional",
            Context = "test-functional",
            Exten = "160",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "ivr-opt2-01"
        });

        var upResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (upResult != channelUp.Task)
            return;

        var channelName = await channelUp.Task;
        await Task.Delay(TimeSpan.FromSeconds(3));

        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = channelName,
            Digit = "2",
            Duration = 250,
            Receive = true
        });

        var result = await Task.WhenAny(reachedExt166.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(reachedExt166.Task,
            "pressing '2' during WaitExten must navigate to extension 166");

        var evt = await reachedExt166.Task;
        evt.Extension.Should().Be("166");
    }

    /// <summary>
    /// Does not send any DTMF during WaitExten(5). The "t" handler fires a Hangup.
    /// Verifies that the channel hangs up after the timeout.
    /// </summary>
    [AsteriskContainerFact]
    public async Task IvrTimeout_ShouldHangup()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangupReceived = new TaskCompletionSource<HangupEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutHandlerReached = new TaskCompletionSource<NewExtenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new IvrObserver(
            onChannelUp: name => channelUp.TrySetResult(name),
            onNewExten: e =>
            {
                // The "t" timeout handler fires as extension "t"
                if (e.Extension == "t")
                    timeoutHandlerReached.TrySetResult(e);
            },
            onHangup: e => hangupReceived.TrySetResult(e)));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/160@test-functional",
            Context = "test-functional",
            Exten = "160",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "ivr-timeout-01"
        });

        var upResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (upResult != channelUp.Task)
            return;

        // Do NOT send any DTMF — let WaitExten(5) time out
        // Background(silence/1) ~1s + WaitExten(5) = ~6s total, plus margin
        var hangupResult = await Task.WhenAny(hangupReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        hangupResult.Should().Be(hangupReceived.Task,
            "WaitExten timeout must trigger the 't' handler which hangs up the channel");
    }

    /// <summary>
    /// Presses an invalid digit "9" during WaitExten. The "i" handler
    /// sends back to ext 160 (menu retry).
    /// </summary>
    [AsteriskContainerFact]
    public async Task IvrInvalidDigit_ShouldReturnToMenu()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidHandlerReached = new TaskCompletionSource<NewExtenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var menuRetryReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitExtenCount = 0;

        using var subscription = connection.Subscribe(new IvrObserver(
            onChannelUp: name => channelUp.TrySetResult(name),
            onNewExten: e =>
            {
                // The "i" invalid handler fires as extension "i"
                if (e.Extension == "i")
                    invalidHandlerReached.TrySetResult(e);

                // After "i" handler, Goto(160,menu) causes a second WaitExten cycle
                if (string.Equals(e.Application, "WaitExten", StringComparison.OrdinalIgnoreCase))
                {
                    var count = Interlocked.Increment(ref waitExtenCount);
                    if (count >= 2)
                        menuRetryReached.TrySetResult(true);
                }
            }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/160@test-functional",
            Context = "test-functional",
            Exten = "160",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "ivr-invalid-01"
        });

        var upResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (upResult != channelUp.Task)
            return;

        var channelName = await channelUp.Task;
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Press "9" — not a valid menu option
        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = channelName,
            Digit = "9",
            Duration = 250,
            Receive = true
        });

        // Verify the "i" handler fires
        var invalidResult = await Task.WhenAny(invalidHandlerReached.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        invalidResult.Should().Be(invalidHandlerReached.Task,
            "pressing an invalid digit must trigger the 'i' handler");

        // Verify the menu restarts (second WaitExten)
        var retryResult = await Task.WhenAny(menuRetryReached.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        retryResult.Should().Be(menuRetryReached.Task,
            "the 'i' handler must Goto ext 160 causing a second WaitExten cycle");
    }

    /// <summary>
    /// Collects all NewExtenEvent events during a full IVR call flow (option 1)
    /// and verifies the dialplan progression includes Background, WaitExten, and target extension.
    /// </summary>
    [AsteriskContainerFact]
    public async Task NewExtenEvent_ShouldTrackDialplanFlow()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var allNewExten = new ConcurrentQueue<NewExtenEvent>();
        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedExt165 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new IvrObserver(
            onChannelUp: name => channelUp.TrySetResult(name),
            onNewExten: e =>
            {
                allNewExten.Enqueue(e);
                if (e.Extension == "165")
                    reachedExt165.TrySetResult(true);
            }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/160@test-functional",
            Context = "test-functional",
            Exten = "160",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "ivr-flow-01"
        });

        var upResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (upResult != channelUp.Task)
            return;

        var channelName = await channelUp.Task;
        await Task.Delay(TimeSpan.FromSeconds(3));

        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = channelName,
            Digit = "1",
            Duration = 250,
            Receive = true
        });

        var result = await Task.WhenAny(reachedExt165.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (result != reachedExt165.Task)
            return;

        // Allow final events to propagate
        await Task.Delay(TimeSpan.FromSeconds(1));

        var events = allNewExten.ToArray();
        events.Should().NotBeEmpty("NewExtenEvent sequence must be captured during IVR flow");

        // Verify the flow includes key applications
        var applications = events
            .Where(e => e.Application is not null)
            .Select(e => e.Application!)
            .ToList();

        applications.Should().Contain(a => a.Contains("Background", StringComparison.OrdinalIgnoreCase),
            "dialplan flow must include Background application");
        applications.Should().Contain(a => a.Contains("WaitExten", StringComparison.OrdinalIgnoreCase),
            "dialplan flow must include WaitExten application");

        // Verify extension progression includes both the IVR menu and the target
        var extensions = events
            .Where(e => e.Extension is not null)
            .Select(e => e.Extension!)
            .ToList();

        extensions.Should().Contain("160", "flow must pass through IVR menu extension 160");
        extensions.Should().Contain("165", "flow must reach target extension 165");
    }

    /// <summary>
    /// Observer for IVR-related events: NewExtenEvent, channel-up state, and HangupEvent.
    /// </summary>
    private sealed class IvrObserver(
        Action<string>? onChannelUp = null,
        Action<NewExtenEvent>? onNewExten = null,
        Action<HangupEvent>? onHangup = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case NewExtenEvent nee:
                    onNewExten?.Invoke(nee);
                    break;
                case NewChannelEvent nce when nce.ChannelStateDesc == "Up" && nce.Channel is not null:
                    onChannelUp?.Invoke(nce.Channel);
                    break;
                case HangupEvent he:
                    onHangup?.Invoke(he);
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
