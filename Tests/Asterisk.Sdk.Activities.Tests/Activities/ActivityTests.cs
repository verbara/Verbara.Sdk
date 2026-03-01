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
    public async Task BlindTransferActivity_ShouldSetContextAndGoto()
    {
        var activity = new BlindTransferActivity(_channel)
        {
            Destination = new DialPlanExtension("from-internal", "3000", 1)
        };

        await activity.StartAsync();

        await _channel.Received(1).SetVariableAsync("TRANSFER_CONTEXT", "from-internal", Arg.Any<CancellationToken>());
        await _channel.Received(1).ExecAsync("Goto", "from-internal,3000,1", Arg.Any<CancellationToken>());
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
}
