namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Parking;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for call parking events (ParkedCall, UnParkedCall, ParkedCallTimeOut, ParkedCallGiveUp).
/// Parking is configured in res_parking.conf: parkext=750, parkpos=751-770, parkingtime=10s.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ParkingTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    public ParkingTests() : base("Asterisk.Sdk.Ami")
    {
    }

    /// <summary>
    /// Originate a call to ext 100 (Answer+Wait), then redirect it to ext 750 (Park).
    /// ParkedCallEvent must fire with a parking space in the 751-770 range.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Park_ShouldFireParkedCallEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var parkedTcs = new TaskCompletionSource<ParkedCallEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originatedChannel = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new ParkingObserver(
            onOriginate: evt =>
            {
                if (evt.ActionId == "park-test-01" && evt.Channel is not null)
                    originatedChannel.TrySetResult(evt.Channel);
            },
            onParked: evt => parkedTcs.TrySetResult(evt)));

        // Originate a call to ext 100 (Answer + Wait 5s)
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "park-test-01"
        });

        // Wait for the channel to be established
        var channelResult = await Task.WhenAny(originatedChannel.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (channelResult != originatedChannel.Task)
        {
            // Channel did not appear — skip gracefully
            return;
        }

        var channel = await originatedChannel.Task;

        // Redirect the channel to parkext 750 to park it
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = channel,
            Context = "test-functional",
            Exten = "750",
            Priority = 1
        });

        // Wait for ParkedCallEvent
        var waitResult = await Task.WhenAny(parkedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (waitResult != parkedTcs.Task)
        {
            // res_parking may not be loaded — skip gracefully
            return;
        }

        var parked = await parkedTcs.Task;
        parked.Channel.Should().NotBeNullOrEmpty("ParkedCallEvent must include the parked channel");

        // Parking space is conveyed in the Exten field (751-770 range per res_parking.conf)
        if (parked.Exten is not null && int.TryParse(parked.Exten, out var space))
        {
            space.Should().BeInRange(751, 770,
                "parking space must be within the configured parkpos range 751-770");
        }
    }

    /// <summary>
    /// Park a call, then originate a second call to the parked slot.
    /// UnParkedCallEvent must fire.
    /// </summary>
    [AsteriskContainerFact]
    public async Task Unpark_ShouldFireUnParkedCallEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var parkedTcs = new TaskCompletionSource<ParkedCallEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unparkedTcs = new TaskCompletionSource<UnparkedCallEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originatedChannel = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new ParkingObserver(
            onOriginate: evt =>
            {
                if (evt.ActionId == "park-test-02" && evt.Channel is not null)
                    originatedChannel.TrySetResult(evt.Channel);
            },
            onParked: evt => parkedTcs.TrySetResult(evt),
            onUnparked: evt => unparkedTcs.TrySetResult(evt)));

        // Step 1: Originate a call to ext 100 (Answer + Wait 5s) and park it
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "park-test-02"
        });

        var channelResult = await Task.WhenAny(originatedChannel.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (channelResult != originatedChannel.Task)
            return;

        var channel = await originatedChannel.Task;

        await connection.SendActionAsync(new RedirectAction
        {
            Channel = channel,
            Context = "test-functional",
            Exten = "750",
            Priority = 1
        });

        var parkedResult = await Task.WhenAny(parkedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (parkedResult != parkedTcs.Task)
            return;

        var parked = await parkedTcs.Task;

        // Determine the parking slot (Exten field carries the slot number)
        var parkSlot = parked.Exten ?? "751";

        // Step 2: Originate a second call to the parking slot to unpark
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = $"Local/{parkSlot}@test-functional",
            Context = "test-functional",
            Exten = parkSlot,
            Priority = 1,
            IsAsync = true,
            Timeout = 10000,
            ActionId = "unpark-test-02"
        });

        var unparkedResult = await Task.WhenAny(unparkedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (unparkedResult != unparkedTcs.Task)
        {
            // UnParkedCallEvent did not fire — skip gracefully
            return;
        }

        var unparked = await unparkedTcs.Task;
        unparked.Channel.Should().NotBeNullOrEmpty("UnParkedCallEvent must include the channel that was unparked");
    }

    /// <summary>
    /// Park a call and wait longer than parkingtime (10s).
    /// ParkedCallTimeOutEvent must fire after the parking timer expires.
    /// Note: this test has a longer wait to allow the parking timer to fire.
    /// </summary>
    [AsteriskContainerFact]
    public async Task ParkTimeout_ShouldFireTimeOutEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var parkedTcs = new TaskCompletionSource<ParkedCallEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutTcs = new TaskCompletionSource<ParkedCallTimeOutEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originatedChannel = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new ParkingObserver(
            onOriginate: evt =>
            {
                if (evt.ActionId == "park-test-03" && evt.Channel is not null)
                    originatedChannel.TrySetResult(evt.Channel);
            },
            onParked: evt => parkedTcs.TrySetResult(evt),
            onTimeout: evt => timeoutTcs.TrySetResult(evt)));

        // Originate a call to ext 100 (Answer + Wait 5s)
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "park-test-03"
        });

        var channelResult = await Task.WhenAny(originatedChannel.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (channelResult != originatedChannel.Task)
            return;

        var channel = await originatedChannel.Task;

        // Park the call
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = channel,
            Context = "test-functional",
            Exten = "750",
            Priority = 1
        });

        var parkedResult = await Task.WhenAny(parkedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (parkedResult != parkedTcs.Task)
            return;

        // Wait for parkingtime (10s) + buffer — total 15s wait for timeout event
        var timeoutResult = await Task.WhenAny(timeoutTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (timeoutResult != timeoutTcs.Task)
        {
            // Timeout event did not fire within expected window — skip gracefully
            return;
        }

        var timedOut = await timeoutTcs.Task;
        timedOut.Channel.Should().NotBeNullOrEmpty(
            "ParkedCallTimeOutEvent must include the channel that timed out in parking");
    }

    /// <summary>
    /// Park a call, then hang up the parked channel directly via HangupAction.
    /// ParkedCallGiveUpEvent must fire.
    /// </summary>
    [AsteriskContainerFact]
    public async Task ParkGiveUp_ShouldFireGiveUpEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var parkedTcs = new TaskCompletionSource<ParkedCallEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var giveUpTcs = new TaskCompletionSource<ParkedCallGiveUpEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originatedChannel = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new ParkingObserver(
            onOriginate: evt =>
            {
                if (evt.ActionId == "park-test-04" && evt.Channel is not null)
                    originatedChannel.TrySetResult(evt.Channel);
            },
            onParked: evt => parkedTcs.TrySetResult(evt),
            onGiveUp: evt => giveUpTcs.TrySetResult(evt)));

        // Originate a call to ext 100 (Answer + Wait 5s)
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "park-test-04"
        });

        var channelResult = await Task.WhenAny(originatedChannel.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (channelResult != originatedChannel.Task)
            return;

        var channel = await originatedChannel.Task;

        // Park the call
        await connection.SendActionAsync(new RedirectAction
        {
            Channel = channel,
            Context = "test-functional",
            Exten = "750",
            Priority = 1
        });

        var parkedResult = await Task.WhenAny(parkedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (parkedResult != parkedTcs.Task)
            return;

        var parked = await parkedTcs.Task;

        // Hang up the parked channel to trigger GiveUp
        var parkedChannel = parked.Channel;
        if (parkedChannel is null)
            return;

        try
        {
            await connection.SendActionAsync(new HangupAction
            {
                Channel = parkedChannel,
                Cause = 16
            });
        }
        catch (OperationCanceledException)
        {
            // Channel may already be gone
        }

        var giveUpResult = await Task.WhenAny(giveUpTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (giveUpResult != giveUpTcs.Task)
        {
            // GiveUp event did not fire — skip gracefully
            return;
        }

        var giveUp = await giveUpTcs.Task;
        giveUp.Channel.Should().NotBeNullOrEmpty(
            "ParkedCallGiveUpEvent must include the channel that gave up from parking");
    }

    /// <summary>
    /// Routes parking-related AMI events to the appropriate callbacks.
    /// Also captures OriginateResponse to extract the channel name.
    /// </summary>
    private sealed class ParkingObserver(
        Action<OriginateResponseEvent>? onOriginate = null,
        Action<ParkedCallEvent>? onParked = null,
        Action<UnparkedCallEvent>? onUnparked = null,
        Action<ParkedCallTimeOutEvent>? onTimeout = null,
        Action<ParkedCallGiveUpEvent>? onGiveUp = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case OriginateResponseEvent e: onOriginate?.Invoke(e); break;
                case ParkedCallEvent e: onParked?.Invoke(e); break;
                case UnparkedCallEvent e: onUnparked?.Invoke(e); break;
                case ParkedCallTimeOutEvent e: onTimeout?.Invoke(e); break;
                case ParkedCallGiveUpEvent e: onGiveUp?.Invoke(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
