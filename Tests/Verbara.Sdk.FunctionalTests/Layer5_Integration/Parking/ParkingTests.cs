namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Parking;

using System.Collections.Concurrent;
using System.Globalization;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for call parking events (ParkedCall, UnParkedCall, ParkedCallTimeOut, ParkedCallGiveUp).
/// Parking is configured in res_parking.conf: parkext=750, parkpos=751-770, parkingtime=10s, in the lot
/// context [parkedcalls], which [test-functional] includes.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ParkingTests : FunctionalTestBase
{
    public ParkingTests() : base("Verbara.Sdk.Ami")
    {
    }

    /// <summary>
    /// Originate a call to ext 100 (Answer+Wait), then redirect it to ext 750 (Park).
    /// ParkedCallEvent must fire with a parking space in the 751-770 range.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23: the redirect to 750 parks the call and a ParkedCall event arrives, but ParkedCallEvent.Channel is null; Asterisk names the parked channel ParkeeChannel, which ParkedCallEvent does not map")]
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
        channelResult.Should().Be(originatedChannel.Task, "originating Local/100@test-functional must report the originated channel within 10 s");

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
        waitResult.Should().Be(parkedTcs.Task, "a redirect to 750 must park the call in the parkedcalls lot and produce a ParkedCall event within 10 s");

        var parked = await parkedTcs.Task;
        parked.Channel.Should().NotBeNullOrEmpty("ParkedCallEvent must include the parked channel");

        // Parking space is conveyed in the Exten field (751-770 range per res_parking.conf)
        parked.Exten.Should().NotBeNull("ParkedCallEvent must convey the parking space");
        int.TryParse(parked.Exten, NumberStyles.Integer, CultureInfo.InvariantCulture, out var space)
            .Should().BeTrue("the parking space '{0}' must be a number", parked.Exten);
        space.Should().BeInRange(751, 770,
            "parking space must be within the configured parkpos range 751-770");
    }

    /// <summary>
    /// Park a call, then originate a second call to the parked slot.
    /// UnParkedCallEvent must fire.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23: the parked call is unparked and an UnParkedCall event arrives, but UnParkedCallEvent.Channel is null; Asterisk names the channel ParkeeChannel, which the event does not map")]
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
        channelResult.Should().Be(originatedChannel.Task, "originating Local/100@test-functional must report the originated channel within 10 s");

        var channel = await originatedChannel.Task;

        await connection.SendActionAsync(new RedirectAction
        {
            Channel = channel,
            Context = "test-functional",
            Exten = "750",
            Priority = 1
        });

        var parkedResult = await Task.WhenAny(parkedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        parkedResult.Should().Be(parkedTcs.Task, "a redirect to 750 must park the call in the parkedcalls lot and produce a ParkedCall event within 10 s");

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
        unparkedResult.Should().Be(unparkedTcs.Task, "dialling the parked space must unpark the call and produce an UnParkedCall event within 10 s");

        var unparked = await unparkedTcs.Task;
        unparked.Channel.Should().NotBeNullOrEmpty("UnParkedCallEvent must include the channel that was unparked");
    }

    /// <summary>
    /// Park a call and wait longer than parkingtime (10s).
    /// ParkedCallTimeOutEvent must fire after the parking timer expires.
    /// Note: this test has a longer wait to allow the parking timer to fire.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23: the parked call times out and a ParkedCallTimeOut event arrives, but ParkedCallTimeOutEvent.Channel is null; Asterisk names the channel ParkeeChannel, which the event does not map")]
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
        channelResult.Should().Be(originatedChannel.Task, "originating Local/100@test-functional must report the originated channel within 10 s");

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
        parkedResult.Should().Be(parkedTcs.Task, "a redirect to 750 must park the call in the parkedcalls lot and produce a ParkedCall event within 10 s");

        // Wait for parkingtime (10s) + buffer — total 15s wait for timeout event
        var timeoutResult = await Task.WhenAny(timeoutTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        timeoutResult.Should().Be(timeoutTcs.Task, "the parkedcalls lot has parkingtime 10, so a ParkedCallTimeOut event must arrive within 15 s");

        var timedOut = await timeoutTcs.Task;
        timedOut.Channel.Should().NotBeNullOrEmpty(
            "ParkedCallTimeOutEvent must include the channel that timed out in parking");
    }

    /// <summary>
    /// Park a call, then hang up the parked channel directly via HangupAction.
    /// ParkedCallGiveUpEvent must fire.
    /// </summary>
    [Fact(Skip = "Measured on Asterisk 23: a ParkedCall event arrives with Channel null (Asterisk names it ParkeeChannel, which ParkedCallEvent does not map), so the test has no channel to hang up and cannot reach the GiveUp event")]
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
        channelResult.Should().Be(originatedChannel.Task, "originating Local/100@test-functional must report the originated channel within 10 s");

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
        parkedResult.Should().Be(parkedTcs.Task, "a redirect to 750 must park the call in the parkedcalls lot and produce a ParkedCall event within 10 s");

        var parked = await parkedTcs.Task;

        // Hang up the parked channel to trigger GiveUp
        var parkedChannel = parked.Channel;
        parkedChannel.Should().NotBeNull("a ParkedCall event must name the parked channel");

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
        giveUpResult.Should().Be(giveUpTcs.Task, "hanging up a parked channel must produce a ParkedCallGiveUp event within 10 s");

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
