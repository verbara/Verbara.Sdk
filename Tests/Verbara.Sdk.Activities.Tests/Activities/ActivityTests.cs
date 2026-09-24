using Verbara.Sdk;
using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Activities.Models;
using Verbara.Sdk.Ari.Audio;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Activities.Tests.Activities;

public class ActivityTests
{
    private readonly IAgiChannel _channel = Substitute.For<IAgiChannel>();

    [Fact]
    public async Task DialActivity_ShouldCallExecDial()
    {
        var activity = new DialActivity(_channel)
        {
            Target = new EndPoint(TechType.PJSIP, "2000"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("Dial", Arg.Is<string>(s => s!.Contains("PJSIP/2000")), Arg.Any<CancellationToken>());
        activity.Status.Should().Be(ActivityStatus.Completed);
    }

    [Fact]
    public async Task DialActivity_ShouldCaptureDialStatus_AfterExec()
    {
#pragma warning disable CA2012
        _channel.GetVariableAsync("DIALSTATUS", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("ANSWER"));
#pragma warning restore CA2012

        var activity = new DialActivity(_channel)
        {
            Target = new EndPoint(TechType.PJSIP, "100"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        await activity.StartAsync();

        activity.DialStatus.Should().Be("ANSWER");
    }

    [Fact]
    public async Task HoldActivity_ShouldCallMusicOnHold()
    {
        var activity = new HoldActivity(_channel) { MusicOnHoldClass = "jazz" };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("MusicOnHold", "jazz", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BridgeActivity_ShouldCallBridge()
    {
        var activity = new BridgeActivity(_channel) { TargetChannel = "PJSIP/3000-002" };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("Bridge", "PJSIP/3000-002", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HangupActivity_ShouldCallHangup()
    {
        var activity = new HangupActivity(_channel);

        await activity.StartAsync();

        await _channel.Received(1).HangupAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HangupActivity_ShouldPassCauseCode_WhenProvided()
    {
        var activity = new HangupActivity(_channel) { CauseCode = 16 };
        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("Hangup", "16", Arg.Any<CancellationToken>());
        await _channel.DidNotReceive().HangupAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayMessageActivity_ShouldStreamFile()
    {
        var activity = new PlayMessageActivity(_channel) { FileName = "welcome" };

        await activity.StartAsync();

        await _channel.Received(1).StreamFileAsync("welcome", "", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueActivity_ShouldCallQueue()
    {
        var activity = new QueueActivity(_channel) { QueueName = "sales" };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("Queue", "sales", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueActivity_ShouldBuildCorrectArgs_WithTimeoutInPosition5()
    {
#pragma warning disable CA2012
        _channel.GetVariableAsync("QUEUESTATUS", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("TIMEOUT"));
#pragma warning restore CA2012

        var activity = new QueueActivity(_channel)
        {
            QueueName = "sales",
            Options = "t",
            Timeout = TimeSpan.FromSeconds(60)
        };
        await activity.StartAsync();

        // Queue(queuename,options,URL,announceoverride,timeout)
        await _channel.Received(1).ExecAsync("Queue", "sales,t,,,60", Arg.Any<CancellationToken>());
        activity.QueueStatus.Should().Be("TIMEOUT");
    }

    [Fact]
    public async Task MeetmeActivity_ShouldUseConfBridgeByDefault()
    {
        var activity = new MeetmeActivity(_channel) { RoomNumber = "100" };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ConfBridge", "100", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MeetmeActivity_WithLegacyMeetMe()
    {
        var activity = new MeetmeActivity(_channel) { RoomNumber = "100", UseConfBridge = false };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("MeetMe", "100", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VoicemailActivity_ShouldCallVoiceMail()
    {
        var activity = new VoicemailActivity(_channel) { Mailbox = "2000", Context = "default" };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("VoiceMail", "2000@default,", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlindTransferActivity_ShouldUseBlindTransferApp_NotGoto()
    {
        var channel = Substitute.For<IAgiChannel>();
        var destination = new DialPlanExtension("from-internal", "2000", 1);
        var activity = new BlindTransferActivity(channel) { Destination = destination };

        await activity.StartAsync();

        await channel.Received(1).ExecAsync("BlindTransfer", "2000@from-internal", Arg.Any<CancellationToken>());
        await channel.DidNotReceive().ExecAsync("Goto", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlindTransferActivity_ShouldFormatDestinationCorrectly()
    {
        var channel = Substitute.For<IAgiChannel>();
        var destination = new DialPlanExtension("sales-queue", "3500", 1);
        var activity = new BlindTransferActivity(channel) { Destination = destination };

        await activity.StartAsync();

        await channel.Received(1).ExecAsync("BlindTransfer", "3500@sales-queue", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParkActivity_ShouldUseCorrectParkArgs()
    {
        var activity = new ParkActivity(_channel) { ParkingLot = "premium" };
        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("Park", "premium", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParkActivity_ShouldUseEmptyArgs_WhenNoParkingLot()
    {
        var activity = new ParkActivity(_channel);
        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("Park", "", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Activity_StatusChanges_ShouldTrackLifecycle()
    {
        var statuses = new List<ActivityStatus>();
        var activity = new HangupActivity(_channel);
        activity.StatusChanges.Subscribe(s => statuses.Add(s));

        await activity.StartAsync();
        await activity.DisposeAsync();

        statuses.Should().ContainInOrder(
            ActivityStatus.Pending,
            ActivityStatus.Starting,
            ActivityStatus.InProgress,
            ActivityStatus.Completed);
    }

    [Fact]
    public async Task CancelAsync_ShouldActuallyCancelRunningExecution()
    {
        var channel = Substitute.For<IAgiChannel>();

        // Mock ExecAsync to hang until cancelled
#pragma warning disable CA2012
        channel.ExecAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                return new ValueTask(Task.Delay(Timeout.Infinite, ct));
            });
#pragma warning restore CA2012

        var activity = new DialActivity(channel)
        {
            Target = new EndPoint(TechType.PJSIP, "100"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        var startTask = activity.StartAsync().AsTask();

        // Wait for InProgress
        await Task.Delay(50);
        activity.Status.Should().Be(ActivityStatus.InProgress);

        // Cancel
        await activity.CancelAsync();

        // Should complete (not hang)
        await startTask;
        activity.Status.Should().Be(ActivityStatus.Cancelled);
    }

    [Fact]
    public async Task CancelAsync_ShouldCancelCtsBeforeSettingStatus_WhenActivityIsRunning()
    {
        // Arrange — activity that blocks until cancelled, recording whether
        // the CTS was already cancelled at the moment status becomes Cancelled.
        var channel = Substitute.For<IAgiChannel>();
        var ctsWasCancelledWhenStatusChanged = false;
        CancellationToken capturedToken = default;

#pragma warning disable CA2012
        channel.ExecAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedToken = callInfo.ArgAt<CancellationToken>(2);
                return new ValueTask(Task.Delay(Timeout.Infinite, capturedToken));
            });
#pragma warning restore CA2012

        var activity = new DialActivity(channel) { Target = new EndPoint(TechType.PJSIP, "1000") };

        // Subscribe to observe the exact moment Cancelled status is emitted
        activity.StatusChanges.Subscribe(s =>
        {
            if (s == ActivityStatus.Cancelled)
                ctsWasCancelledWhenStatusChanged = capturedToken.IsCancellationRequested;
        });

        var startTask = activity.StartAsync().AsTask();

        // Act — wait for InProgress, then cancel
        await Task.Delay(50);
        await activity.CancelAsync();
        await startTask;

        // Assert — the CTS must have been cancelled BEFORE the status was set
        activity.Status.Should().Be(ActivityStatus.Cancelled);
        ctsWasCancelledWhenStatusChanged.Should().BeTrue(
            "the CancellationToken should be cancelled before status transitions to Cancelled");
    }

    [Fact]
    public async Task StartAsync_ShouldThrow_WhenCalledTwice()
    {
        var activity = new HangupActivity(_channel);
        await activity.StartAsync();

        var act = () => activity.StartAsync().AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be started from Completed*");
    }

    [Fact]
    public void ExternalMediaActivity_ShouldInheritFromAriActivityBase()
    {
        var ariClient = Substitute.For<IAriClient>();
        var activity = new ExternalMediaActivity(ariClient) { App = "test", ExternalHost = "127.0.0.1:9092" };
        activity.Should().BeAssignableTo<AriActivityBase>();
        activity.Should().BeAssignableTo<IActivity>();
    }

    [Fact]
    public void ExternalMediaActivity_ShouldSetStatusCorrectly_WhenCreated()
    {
        var ariClient = Substitute.For<IAriClient>();
        var activity = new ExternalMediaActivity(ariClient) { App = "test", ExternalHost = "127.0.0.1:9092" };
        activity.Status.Should().Be(ActivityStatus.Pending);
    }

    [Fact]
    public async Task AriActivityBase_ShouldThrow_WhenStartedTwice()
    {
        var ariClient = Substitute.For<IAriClient>();
        var channelsResource = Substitute.For<IAriChannelsResource>();
        ariClient.Channels.Returns(channelsResource);
#pragma warning disable CA2012
        channelsResource.CreateExternalMediaAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AriChannel>(new AriChannel { Id = "ch-1" }));
#pragma warning restore CA2012

        // Activity will fail at polling (no audio server) but we test the second start
        var activity = new ExternalMediaActivity(ariClient) { App = "test", ExternalHost = "127.0.0.1:9092", ConnectionTimeout = TimeSpan.FromMilliseconds(50) };

        // First start will throw TimeoutException (no audio server connected)
        var firstStart = () => activity.StartAsync().AsTask();
        await firstStart.Should().ThrowAsync<TimeoutException>();

        // Second start should throw InvalidOperationException (status is Failed)
        var secondStart = () => activity.StartAsync().AsTask();
        await secondStart.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be started from Failed*");
    }

    [Fact]
    public async Task CancelAsync_ShouldBeIdempotent_WhenCalledTwice()
    {
        var channel = Substitute.For<IAgiChannel>();

        // Mock ExecAsync to hang until cancelled
#pragma warning disable CA2012
        channel.ExecAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                return new ValueTask(Task.Delay(Timeout.Infinite, ct));
            });
#pragma warning restore CA2012

        var activity = new DialActivity(channel)
        {
            Target = new EndPoint(TechType.PJSIP, "100"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        var startTask = activity.StartAsync().AsTask();

        // Wait for InProgress
        await Task.Delay(50);
        activity.Status.Should().Be(ActivityStatus.InProgress);

        // Cancel twice — second call should be a no-op
        await activity.CancelAsync();
        var secondCancel = async () => await activity.CancelAsync();
        await secondCancel.Should().NotThrowAsync("calling CancelAsync a second time should be idempotent");

        await startTask;
        activity.Status.Should().Be(ActivityStatus.Cancelled);
    }

    [Fact]
    public async Task ExternalMediaActivity_CancelAsync_ShouldHangupChannel()
    {
        var ariClient = Substitute.For<IAriClient>();
        var channelsResource = Substitute.For<IAriChannelsResource>();
        ariClient.Channels.Returns(channelsResource);

#pragma warning disable CA2012
        channelsResource.CreateExternalMediaAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Return a channel, then simulate the audio server never connecting
                return new ValueTask<AriChannel>(new AriChannel { Id = "ext-ch-1" });
            });
#pragma warning restore CA2012

        var activity = new ExternalMediaActivity(ariClient)
        {
            App = "test",
            ExternalHost = "127.0.0.1:9092",
            ConnectionTimeout = TimeSpan.FromSeconds(30)
        };

        var startTask = activity.StartAsync().AsTask();

        // Wait for InProgress (the activity should be polling for audio connection)
        await Task.Delay(100);
        activity.Status.Should().Be(ActivityStatus.InProgress);

        // Cancel — should trigger OnCancellingAsync which calls HangupAsync
        await activity.CancelAsync();

        // The start task should complete (cancelled)
        await startTask;
        activity.Status.Should().Be(ActivityStatus.Cancelled);

        // Verify HangupAsync was called on the channel
        await channelsResource.Received(1).HangupAsync("ext-ch-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ShouldSendDataAndTcpTransport_WhenEncapsulationIsAudioSocket()
    {
        // ARGUMENT-CAPTURE, deliberately. The tempting test — "GetStream(Channel.Id) returns a
        // stream" — cannot fail here: with a substituted resource the test picks BOTH the Channel.Id
        // handed back AND the uuid its own fake client would send, so it can be made green against
        // the unfixed activity. That closed loop is the defect this change exists to correct.
        // What a real Asterisk rejects is the REQUEST, before any stream exists:
        //   RUN G (probe-capture.txt) — encapsulation=audiosocket, no transport
        //       -> HTTP 400 "transport must be 'tcp' for audiosocket encapsulation"
        //   RUN D (probe-capture.txt) — encapsulation=audiosocket&transport=tcp, no data
        //       -> HTTP 400 "data can not be empty"
        // So the assertion is on the arguments that leave the activity.
        var ariClient = Substitute.For<IAriClient>();
        var channelsResource = Substitute.For<IAriChannelsResource>();
        ariClient.Channels.Returns(channelsResource);

        string? sentEncapsulation = null;
        string? sentTransport = null;
        string? sentData = null;

#pragma warning disable CA2012
        channelsResource.CreateExternalMediaAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            encapsulation: Arg.Any<string?>(), transport: Arg.Any<string?>(),
            connectionType: Arg.Any<string?>(), direction: Arg.Any<string?>(),
            data: Arg.Any<string?>(),
            // NAMED, and the token especially. The fix inserts channelId between data and the token,
            // so a ninth POSITIONAL Arg.Any<CancellationToken>() would bind to a string? parameter
            // and this file would stop compiling — CS1503, the same trap the two substitutes above
            // carry. Naming the token survives the insertion; naming channelId is impossible here,
            // because it does not exist on the signature under test.
            cancellationToken: Arg.Any<CancellationToken>())
            // ForAnyArgs, not Returns: the fix adds a channelId parameter to this method, and an
            // argument-by-argument match would leave it compared against its default null — the
            // setup would stop matching the moment the activity starts sending one, and the test
            // would fail on a null channel rather than on its own assertions.
            .ReturnsForAnyArgs(callInfo =>
            {
                // Positional, because this test may not NAME channelId: that parameter does not
                // exist on the signature under test, and naming it is CS1739 — a compile error, not
                // a failing assertion. The captured positions are stable across the fix, which
                // appends channelId after data and before the CancellationToken:
                //   0 app, 1 externalHost, 2 format, 3 encapsulation, 4 transport,
                //   5 connectionType, 6 direction, 7 data, (8 channelId), last CancellationToken.
                sentEncapsulation = callInfo.ArgAt<string?>(3);
                sentTransport = callInfo.ArgAt<string?>(4);
                sentData = callInfo.ArgAt<string?>(7);
                return new ValueTask<AriChannel>(new AriChannel { Id = "ext-ch-audiosocket" });
            });
#pragma warning restore CA2012

        // Never started: the activity only reads GetStream off it, and an unbound server needs no
        // port. Nothing will ever connect, so the run ends in the timeout below — that is how the
        // run ends, not what this test measures.
        await using var audioSocketServer = new AudioSocketServer(
            new AudioServerOptions { AudioSocketPort = 0, ListenAddress = "127.0.0.1" },
            NullLogger<AudioSocketServer>.Instance);

        var activity = new ExternalMediaActivity(ariClient, audioSocketServer)
        {
            App = "test",
            ExternalHost = "127.0.0.1:19099",
            Encapsulation = "audiosocket",
            ConnectionTimeout = TimeSpan.FromMilliseconds(50)
        };

        var start = () => activity.StartAsync().AsTask();
        await start.Should().ThrowAsync<TimeoutException>(
            "nothing connects to the audio server in this test, so the activity times out after "
            + "ConnectionTimeout — the create call it made on the way there is what is asserted below");

        // Positive control, and it is load-bearing. Everything below asserts that a captured value is
        // null, and a capture that never ran reads null too — so without one captured value that MUST
        // be non-null today, a broken offset or an unmatched substitute would go red in exactly the
        // shape of the defect and measure nothing. This repository has shipped that twice.
        sentEncapsulation.Should().Be("audiosocket",
            "the capture must be reading the real argument array: this is the one value the unfixed "
            + "activity already sends, so if it does not arrive the nulls below prove nothing");

        sentData.Should().NotBeNull(
            "an audiosocket create with no data is HTTP 400 \"data can not be empty\" "
            + "(probe-capture.txt RUN D), so the activity must supply the identification uuid");
        sentTransport.Should().Be("tcp",
            "audiosocket encapsulation on any other transport is HTTP 400 \"transport must be 'tcp' "
            + "for audiosocket encapsulation\" (probe-capture.txt RUN G)");
    }

    [Fact]
    public async Task CancelAsync_ShouldNotThrow_WhenActivityAlreadyCompleted()
    {
        var activity = new HangupActivity(_channel);
        await activity.StartAsync();

        activity.Status.Should().Be(ActivityStatus.Completed);

        // Cancelling an already-completed activity should be a safe no-op
        var act = () => activity.CancelAsync().AsTask();
        await act.Should().NotThrowAsync("cancelling a completed activity should be a no-op");
        activity.Status.Should().Be(ActivityStatus.Completed,
            "status should remain Completed after a late cancel attempt");
    }

    [Fact]
    public async Task CancelAsync_ShouldNotThrow_WhenActivityNeverStarted()
    {
        var activity = new HangupActivity(_channel);

        activity.Status.Should().Be(ActivityStatus.Pending);

        // Cancelling a Pending activity should be a safe no-op (not InProgress or Starting)
        var act = () => activity.CancelAsync().AsTask();
        await act.Should().NotThrowAsync("cancelling a pending activity should be a no-op");
        activity.Status.Should().Be(ActivityStatus.Pending,
            "status should remain Pending when cancel is called before start");
    }
}
