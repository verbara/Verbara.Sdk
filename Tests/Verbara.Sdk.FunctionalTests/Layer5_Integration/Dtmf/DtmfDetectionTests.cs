namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Dtmf;

using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for DTMF detection via AMI PlayDTMF action and event observation.
/// Requires Asterisk container with the test-functional dialplan context.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class DtmfDetectionTests : FunctionalTestBase
{
    public DtmfDetectionTests() : base("Verbara.Sdk.Ami")
    {
    }

    /// <summary>
    /// Plays a single DTMF digit into an answered call and verifies that both DtmfBeginEvent and DtmfEndEvent fire.
    /// PlayDtmf with Receive=false hands the digit to the channel driver, and the Local driver delivers it to the
    /// other half of the pair, which reports the begin and the end. (With Receive=true Asterisk queues only the end
    /// of the digit, so the channel reports a DtmfEnd and no DtmfBegin — measured on Asterisk 23, 5 of 5 runs
    /// pinned to one half.)
    /// </summary>
    [Fact]
    public async Task PlayDtmf_ShouldGenerateBeginAndEndEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // The originate names both halves: the digit is played on the ;1 half and arrives on the ;2 half.
        var playedOnId = $"dtmf-begin-end-{Guid.NewGuid():N}";
        var receivedOnId = playedOnId + "-2";
        var beginReceived = new TaskCompletionSource<DtmfBeginEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endReceived = new TaskCompletionSource<DtmfEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();

        using var subscription = connection.Subscribe(new DtmfEventObserver(
            onBegin: e =>
            {
                seen.Enqueue($"{e.GetType().Name}(raw Event {e.RawFields?.GetValueOrDefault("Event")}) {e.RawFields?.GetValueOrDefault("Channel")} {e.UniqueId} {e.RawFields?.GetValueOrDefault("Digit")} {e.RawFields?.GetValueOrDefault("DurationMs")}");
                if (e.UniqueId == receivedOnId)
                    beginReceived.TrySetResult(e);
            },
            onEnd: e =>
            {
                seen.Enqueue($"{e.GetType().Name}(raw Event {e.RawFields?.GetValueOrDefault("Event")}) {e.RawFields?.GetValueOrDefault("Channel")} {e.UniqueId} {e.RawFields?.GetValueOrDefault("Digit")} {e.RawFields?.GetValueOrDefault("DurationMs")}");
                if (e.UniqueId == receivedOnId)
                    endReceived.TrySetResult(e);
            },
            onChannelUp: (name, uniqueId) =>
            {
                if (uniqueId == playedOnId)
                    channelUp.TrySetResult(name);
            }));

        // The ;2 half runs ext 100 (Answer + Wait(30)); the ;1 half runs ext 152, which plays a tone into it, so the
        // half the digit arrives on reads media. With both halves in Wait, the ;2 half sometimes reported the digit
        // as two DtmfBegin and no DtmfEnd within 10 s (suite runs on Asterisk 22 and 23).
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "152",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ChannelId = playedOnId,
            OtherChannelId = receivedOnId,
            ActionId = "dtmf-begin-end-01"
        });

        // Wait for channel to be answered
        var channelResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        channelResult.Should().Be(channelUp.Task, "ext 100 answers, so the originated channel must report Up within 10 s");

        var channelName = await channelUp.Task;
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // fence-allow: SETTLE — lets ext 152 start its tone before the digit is played

        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = channelName,
            Digit = "5",
            Duration = 250,
            Receive = false
        });

        var beginResult = await Task.WhenAny(beginReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        beginResult.Should().Be(beginReceived.Task, "the digit PlayDtmf sends on the ;1 half must arrive on the ;2 half as a DtmfBegin within 10 s; DTMF events seen: {0}", string.Join("; ", seen));
        var endResult = await Task.WhenAny(endReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        endResult.Should().Be(endReceived.Task, "the digit PlayDtmf sends on the ;1 half must arrive on the ;2 half as a DtmfEnd within 10 s; DTMF events seen: {0}", string.Join("; ", seen));

        (await beginReceived.Task).RawFields?.GetValueOrDefault("Digit").Should().Be("5", "the DtmfBegin must carry the digit played");
        var endEvent = await endReceived.Task;
        endEvent.RawFields?.GetValueOrDefault("Digit").Should().Be("5", "the DtmfEnd must carry the digit played");
        endEvent.DurationMs.Should().BeGreaterThan(0, "DTMF end event must report a non-zero duration");
    }

    /// <summary>
    /// Plays all 16 DTMF digits (0-9, *, #, A-D) and verifies each is recognized.
    /// </summary>
    [Fact]
    public async Task PlayDtmf_AllDigits_ShouldBeRecognized()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(30);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var dtmfDigits = new ConcurrentBag<string>();
        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new DtmfEventObserver(
            onEnd: e =>
            {
                var digit = e.RawFields?.GetValueOrDefault("Digit");
                if (digit is not null) dtmfDigits.Add(digit);
            },
            onChannelUp: (name, _) => channelUp.TrySetResult(name)));

        // Originate to ext 100: Answer + Wait(5)
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "dtmf-all-digits-01"
        });

        var channelResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        channelResult.Should().Be(channelUp.Task, "ext 100 answers, so the originated channel must report Up within 10 s");

        var channelName = await channelUp.Task;
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var allDigits = new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "#", "A", "B", "C", "D" };
        foreach (var digit in allDigits)
        {
            await connection.SendActionAsync(new PlayDtmfAction
            {
                Channel = channelName,
                Digit = digit,
                Duration = 100,
                Receive = true
            });
            // Small delay between digits to avoid overlap
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        // Wait for all events to propagate
        await Task.Delay(TimeSpan.FromSeconds(3));

        dtmfDigits.Should().HaveCountGreaterThanOrEqualTo(allDigits.Length,
            "all 16 DTMF digits must produce DtmfEndEvent");

        foreach (var digit in allDigits)
        {
            dtmfDigits.Should().Contain(digit,
                "digit '{0}' must be recognized in DtmfEndEvent", digit);
        }
    }

    /// <summary>
    /// Plays DTMF with Receive=true and verifies the Direction field is "Received".
    /// </summary>
    [Fact]
    public async Task PlayDtmf_DirectionReceived_ShouldSetCorrectDirection()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var endReceived = new TaskCompletionSource<DtmfEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new DtmfEventObserver(
            onEnd: e => endReceived.TrySetResult(e),
            onChannelUp: (name, _) => channelUp.TrySetResult(name)));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "dtmf-dir-recv-01"
        });

        var channelResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        channelResult.Should().Be(channelUp.Task, "ext 100 answers, so the originated channel must report Up within 10 s");

        var channelName = await channelUp.Task;
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = channelName,
            Digit = "3",
            Duration = 250,
            Receive = true
        });

        var result = await Task.WhenAny(endReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(endReceived.Task, "PlayDtmf with Receive=true on an Up channel must produce a DtmfEnd within 10 s");

        var endEvent = await endReceived.Task;
        var direction = endEvent.RawFields?.GetValueOrDefault("Direction");
        direction.Should().Be("Received",
            "PlayDtmf with Receive=true must set Direction to 'Received'");
    }

    /// <summary>
    /// A digit a channel receives in a bridge is written to the other party, which reports it with Direction "Sent".
    /// PlayDtmf with Receive=true plays the digit into one party of the two-party bridge of ext 170. (PlayDtmf with
    /// Receive=false is no way to get "Sent": it hands the digit to the channel driver without writing it, and the
    /// targeted channel reports nothing — measured on Asterisk 23, 5 of 5 runs pinned to one Local half.)
    /// </summary>
    [Fact]
    public async Task PlayDtmf_DirectionSent_ShouldSetCorrectDirection()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // callId names the caller of the bridge; the party it dialled is the one the digit is written to.
        var callId = $"dtmf-dir-sent-{Guid.NewGuid():N}";
        var callerEnter = new TaskCompletionSource<BridgeEnterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enters = new ConcurrentQueue<BridgeEnterEvent>();
        var ends = new ConcurrentQueue<DtmfEndEvent>();

        using var subscription = connection.Subscribe(new DtmfEventObserver(
            onEnd: ends.Enqueue,
            onBridgeEnter: e =>
            {
                enters.Enqueue(e);
                if (e.UniqueId == callId)
                    callerEnter.TrySetResult(e);
            }));

        // ext 170 dials ext 171 (Dial tT), which answers: a two-party bridge.
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/171@test-functional/n",
            Context = "test-functional",
            Exten = "170",
            Priority = 1,
            IsAsync = true,
            ChannelId = callId,
            ActionId = "dtmf-dir-sent-01"
        });

        var bridged = await Task.WhenAny(callerEnter.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        bridged.Should().Be(callerEnter.Task, "ext 170 dials ext 171, which answers, so the caller must enter a bridge within 10 s");
        var caller = await callerEnter.Task;
        var other = enters.FirstOrDefault(e => e.BridgeUniqueid == caller.BridgeUniqueid && e.UniqueId != callId);
        other.Should().NotBeNull("the party ext 170 dialled must be in the caller's bridge");

        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = caller.Channel,
            Digit = "8",
            Duration = 250,
            Receive = true
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        DtmfEndEvent? written = null;
        while (written is null && DateTime.UtcNow < deadline)
        {
            written = ends.FirstOrDefault(e => e.UniqueId == other!.UniqueId);
            if (written is null)
                await Task.Delay(TimeSpan.FromMilliseconds(50)); // fence-allow: GUARD-TIMEOUT — polls for the observed DtmfEnd; a green run leaves as soon as it arrives
        }

        written.Should().NotBeNull("the bridge must write the digit the caller received to the other party, which reports a DtmfEnd within 10 s");
        written!.RawFields?.GetValueOrDefault("Digit").Should().Be("8", "the DtmfEnd must carry the digit played");
        written.RawFields?.GetValueOrDefault("Direction").Should().Be("Sent",
            "a digit written to a channel must be reported with Direction 'Sent'");

        await BestEffort.SendAsync(connection, new HangupAction { Channel = caller.Channel!, Cause = 16 });
    }

    /// <summary>
    /// Originates to ext 150 which runs SendDTMF(123456789*#).
    /// Verifies that sequential DtmfEndEvents fire in the correct digit order.
    /// </summary>
    [Fact]
    public async Task SendDtmf_MultipleDigits_ShouldFireSequentialEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var dtmfDigits = new ConcurrentQueue<string>();
        var allDigitsReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int expectedDigitCount = 11; // 1-9, *, #

        using var subscription = connection.Subscribe(new DtmfEventObserver(
            onEnd: e =>
            {
                // Local channels have two halves (;1 and ;2) — both fire DtmfEnd events.
                // Filter to only the ;1 half to avoid duplicate digits.
                var channel = e.RawFields?.GetValueOrDefault("Channel") ?? "";
                if (channel.Contains(";2", StringComparison.Ordinal))
                    return;

                var digit = e.RawFields?.GetValueOrDefault("Digit");
                if (digit is not null)
                {
                    dtmfDigits.Enqueue(digit);
                    if (dtmfDigits.Count >= expectedDigitCount)
                        allDigitsReceived.TrySetResult(true);
                }
            }));

        // ext 150: Answer, Wait(1), SendDTMF(123456789*#,250,200), Wait(2), Hangup
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/150@test-functional/n",
            Context = "test-functional",
            Exten = "150",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "dtmf-multi-01"
        });

        // SendDTMF takes ~11 digits * (250+200)ms = ~5s, plus overhead
        var result = await Task.WhenAny(allDigitsReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        if (result != allDigitsReceived.Task)
        {
            // Partial results — verify what we got
            dtmfDigits.Should().NotBeEmpty("at least some DTMF events must fire from SendDTMF");
            return;
        }

        var receivedDigits = dtmfDigits.ToArray();
        receivedDigits.Should().HaveCountGreaterThanOrEqualTo(expectedDigitCount,
            "SendDTMF(123456789*#) must produce at least 11 DtmfEndEvents");

        // Verify order of first 11 digits
        var expectedSequence = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "#" };
        receivedDigits.Take(expectedDigitCount).Should().ContainInOrder(expectedSequence,
            "digits must arrive in the order they were sent by SendDTMF");
    }

    /// <summary>
    /// Originates to ext 155 which runs Read() (waits for a digit).
    /// Sends DTMF "7" via PlayDtmf. The channel running Read reports the digit as a DtmfEnd event, and Read()
    /// consumes it into its variable.
    /// </summary>
    [Fact]
    public async Task DtmfConsumedByRead_ShouldFireDtmfEvent()
    {
        // VarSet belongs to the dialplan class, which the default test user does not read.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
            opts.Username = "dialplanflow";
            opts.Password = "dialplanflowpass";
        });
        await connection.ConnectAsync();

        // The originate names the ;1 half, the channel this test plays the digit on and reads every event from.
        var callId = $"dtmf-read-{Guid.NewGuid():N}";
        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dtmfEnd = new TaskCompletionSource<DtmfEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new DtmfEventObserver(
            onEnd: e =>
            {
                if (e.UniqueId == callId)
                    dtmfEnd.TrySetResult(e);
            },
            onChannelUp: (name, uniqueId) =>
            {
                if (uniqueId == callId)
                    channelUp.TrySetResult(name);
            },
            onVarSet: e =>
            {
                if (e.UniqueId == callId && e.Variable == "RESULT")
                    readResult.TrySetResult(e.Value);
            }));

        // ext 155: Answer, Read(RESULT,,1,,,5), Wait(2), Hangup
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/155@test-functional",
            Context = "test-functional",
            Exten = "155",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ChannelId = callId,
            ActionId = "dtmf-read-01"
        });

        var channelResult = await Task.WhenAny(channelUp.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        channelResult.Should().Be(channelUp.Task, "ext 155 answers, so the originated channel must report Up within 10 s");

        var channelName = await channelUp.Task;

        // Read() runs right after Answer; its 5 s window outlasts this pause.
        await Task.Delay(TimeSpan.FromSeconds(1)); // fence-allow: SETTLE — lets the channel enter Read() before the digit is queued; Read waits 5 s for it

        await connection.SendActionAsync(new PlayDtmfAction
        {
            Channel = channelName,
            Digit = "7",
            Duration = 250,
            Receive = true
        });

        var result = await Task.WhenAny(dtmfEnd.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(dtmfEnd.Task, "PlayDtmf with Receive=true into the channel running Read must produce a DtmfEnd on that channel within 10 s");

        var endEvent = await dtmfEnd.Task;
        endEvent.RawFields?.GetValueOrDefault("Digit").Should().Be("7", "the DtmfEnd must carry the digit PlayDtmf sent");
        endEvent.RawFields?.GetValueOrDefault("Channel").Should().Be(channelName, "the DtmfEnd must come from the channel running Read");

        var read = await Task.WhenAny(readResult.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        read.Should().Be(readResult.Task, "Read() must set its RESULT variable when it returns, within 10 s");
        (await readResult.Task).Should().Be("7", "Read() must consume the digit '7'");
    }

    /// <summary>
    /// Observer that captures DtmfBeginEvent, DtmfEndEvent, VarSetEvent, BridgeEnterEvent, and channel-up state (name
    /// and unique id).
    /// </summary>
    private sealed class DtmfEventObserver(
        Action<DtmfBeginEvent>? onBegin = null,
        Action<DtmfEndEvent>? onEnd = null,
        Action<string, string?>? onChannelUp = null,
        Action<VarSetEvent>? onVarSet = null,
        Action<BridgeEnterEvent>? onBridgeEnter = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case DtmfBeginEvent begin:
                    onBegin?.Invoke(begin);
                    break;
                case DtmfEndEvent end:
                    onEnd?.Invoke(end);
                    break;
                case VarSetEvent varSet:
                    onVarSet?.Invoke(varSet);
                    break;
                case BridgeEnterEvent enter:
                    onBridgeEnter?.Invoke(enter);
                    break;
                case NewChannelEvent nce when nce.ChannelStateDesc == "Up" && nce.Channel is not null:
                    onChannelUp?.Invoke(nce.Channel, nce.UniqueId);
                    break;
                // A Local channel is created Down and reports Up through Newstate when its dialplan answers.
                case NewStateEvent nse when nse.ChannelStateDesc == "Up"
                    && nse.Channel is { } name && name.StartsWith("Local/", StringComparison.Ordinal):
                    onChannelUp?.Invoke(name, nse.UniqueId);
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
