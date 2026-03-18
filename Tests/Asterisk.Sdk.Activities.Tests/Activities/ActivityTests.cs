using Asterisk.Sdk;
using Asterisk.Sdk.Activities.Activities;
using Asterisk.Sdk.Activities.Models;
using FluentAssertions;
using NSubstitute;

namespace Asterisk.Sdk.Activities.Tests.Activities;

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

        await _channel.Received(1).ExecAsync("Dial", Arg.Is<string>(s => s.Contains("PJSIP/2000")), Arg.Any<CancellationToken>());
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
}
