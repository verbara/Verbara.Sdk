using Verbara.Sdk;
using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Activities.Models;
using Verbara.Sdk.Ami.Actions;
using FluentAssertions;
using FluentAssertions.Execution;
using NSubstitute;

namespace Verbara.Sdk.Activities.Tests.Activities;

/// <summary>
/// Unit tests for the contact-center supervision activities added in the Tier A sprint:
/// <see cref="AttendedTransferActivity"/>, <see cref="ChanSpyActivity"/>,
/// <see cref="BargeActivity"/>, <see cref="SnoopActivity"/>.
/// </summary>
public class ContactCenterActivityTests
{
    private readonly IAgiChannel _channel = Substitute.For<IAgiChannel>();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly IAriClient _ari = Substitute.For<IAriClient>();

    // ----- AttendedTransferActivity -----

    [Fact]
    public async Task AttendedTransferActivity_ShouldSendAtxferAction_WhenStarted()
    {
        var activity = new AttendedTransferActivity(_ami)
        {
            Channel = "PJSIP/alice-00001",
            Destination = new DialPlanExtension("internal", "2001"),
        };

        await activity.StartAsync();

        await _ami.Received(1).SendActionAsync(
            Arg.Is<AtxferAction>(a =>
                a!.Channel == "PJSIP/alice-00001"
                && a.Context == "internal"
                && a.Exten == "2001"
                && a.Priority == 1),
            Arg.Any<CancellationToken>());
        activity.Status.Should().Be(ActivityStatus.Completed);
    }

    [Fact]
    public async Task AttendedTransferActivity_ShouldHonorPriority_WhenOverridden()
    {
        var activity = new AttendedTransferActivity(_ami)
        {
            Channel = "PJSIP/alice-00001",
            Destination = new DialPlanExtension("internal", "2001"),
            Priority = 3,
        };

        await activity.StartAsync();

        await _ami.Received(1).SendActionAsync(
            Arg.Is<AtxferAction>(a => a!.Priority == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AttendedTransferActivity_ShouldEndFailedAndThrow_WhenAsteriskRefusesTheAtxfer()
    {
        // The text Asterisk 22 and 23 answer to an Atxfer on a channel that does not exist.
        RespondToAtxfer(new ManagerResponse { Response = "Error", Message = "Channel specified does not exist" });
        var activity = new AttendedTransferActivity(_ami)
        {
            Channel = "PJSIP/nobody-00000000",
            Destination = new DialPlanExtension("internal", "2001"),
        };
        var statuses = new List<ActivityStatus>();
        using var subscription = activity.StatusChanges.Subscribe(statuses.Add);

        var starting = () => activity.StartAsync().AsTask();

        using var scope = new AssertionScope();
        (await starting.Should().ThrowAsync<ActivityFailedException>(
                "a transfer Asterisk refused did not happen"))
            .WithMessage("*Channel specified does not exist*");
        activity.Status.Should().Be(ActivityStatus.Failed);
        statuses.Should().Equal(
            ActivityStatus.Pending, ActivityStatus.Starting, ActivityStatus.InProgress, ActivityStatus.Failed);
    }

    [Fact]
    public async Task AttendedTransferActivity_ShouldEndFailedAndThrow_WhenTheRefusalCarriesNoMessage()
    {
        RespondToAtxfer(new ManagerResponse { Response = "error", Message = null });
        var activity = new AttendedTransferActivity(_ami)
        {
            Channel = "PJSIP/alice-00001",
            Destination = new DialPlanExtension("internal", "2001"),
        };

        var starting = () => activity.StartAsync().AsTask();

        using var scope = new AssertionScope();
        (await starting.Should().ThrowAsync<ActivityFailedException>(
                "a refusal without a message is still a refusal"))
            .WithMessage("*Atxfer*Response: Error*");
        activity.Status.Should().Be(ActivityStatus.Failed);
    }

    [Fact]
    public async Task AttendedTransferActivity_ShouldEndCompleted_WhenAsteriskAcceptsTheAtxfer()
    {
        RespondToAtxfer(new ManagerResponse { Response = "Success", Message = "Atxfer successfully queued" });
        var activity = new AttendedTransferActivity(_ami)
        {
            Channel = "PJSIP/alice-00001",
            Destination = new DialPlanExtension("internal", "2001"),
        };

        var starting = () => activity.StartAsync().AsTask();

        await starting.Should().NotThrowAsync("Asterisk accepted and queued the transfer");
        activity.Status.Should().Be(ActivityStatus.Completed);
    }

    private void RespondToAtxfer(ManagerResponse response)
    {
#pragma warning disable CA2012 // NSubstitute setup: the ValueTask is returned to the activity, which awaits it once.
        _ami.SendActionAsync(Arg.Any<AtxferAction>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ManagerResponse>(response));
#pragma warning restore CA2012
    }

    // ----- ChanSpyActivity -----

    [Fact]
    public async Task ChanSpyActivity_ShouldCallChanSpy_WithTargetAndDefaultMode()
    {
        var activity = new ChanSpyActivity(_channel)
        {
            TargetChannel = "PJSIP/alice",
        };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ChanSpy", "PJSIP/alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChanSpyActivity_ShouldEmitWhisperFlag_WhenModeWhisperOnly()
    {
        var activity = new ChanSpyActivity(_channel)
        {
            TargetChannel = "PJSIP/alice",
            Mode = ChanSpyMode.WhisperOnly,
        };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ChanSpy", "PJSIP/alice,w", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChanSpyActivity_ShouldEmitCoachFlag_WhenModeCoach()
    {
        var activity = new ChanSpyActivity(_channel)
        {
            TargetChannel = "PJSIP/alice",
            Mode = ChanSpyMode.Coach,
        };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ChanSpy", "PJSIP/alice,W", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChanSpyActivity_ShouldCombineModeAndOptions_WhenBothProvided()
    {
        var activity = new ChanSpyActivity(_channel)
        {
            TargetChannel = "PJSIP/alice",
            Mode = ChanSpyMode.SpyOnly,
            Options = "qE",
        };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ChanSpy", "PJSIP/alice,oqE", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChanSpyActivity_ShouldEmitEmptyArgs_WhenNoTargetAndDefaultMode()
    {
        var activity = new ChanSpyActivity(_channel);

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ChanSpy", string.Empty, Arg.Any<CancellationToken>());
    }

    // ----- BargeActivity -----

    [Fact]
    public async Task BargeActivity_ShouldCallChanSpy_WithBargeFlag()
    {
        var activity = new BargeActivity(_channel)
        {
            TargetChannel = "PJSIP/alice",
        };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ChanSpy", "PJSIP/alice,B", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BargeActivity_ShouldCombineBargeAndOptions_WhenOptionsProvided()
    {
        var activity = new BargeActivity(_channel)
        {
            TargetChannel = "PJSIP/alice",
            Options = "qE",
        };

        await activity.StartAsync();

        await _channel.Received(1).ExecAsync("ChanSpy", "PJSIP/alice,BqE", Arg.Any<CancellationToken>());
    }

    // ----- SnoopActivity -----

    [Fact]
    public async Task SnoopActivity_ShouldCallSnoopAsync_AndCaptureChannel()
    {
        var channels = Substitute.For<IAriChannelsResource>();
        _ari.Channels.Returns(channels);

        var snoopChannel = new AriChannel { Id = "snoop-001", Name = "Snoop/snoop-001" };
#pragma warning disable CA2012
        channels.SnoopAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AriChannel>(snoopChannel));
#pragma warning restore CA2012

        var activity = new SnoopActivity(_ari)
        {
            TargetChannelId = "target-001",
            App = "my-stasis",
        };

        await activity.StartAsync();

        await channels.Received(1).SnoopAsync(
            "target-001",
            "my-stasis",
            "both",  // Spy default
            null,    // Whisper default (None)
            null,    // SnoopId default
            Arg.Any<CancellationToken>());
        activity.SnoopChannel.Should().BeSameAs(snoopChannel);
    }

    [Fact]
    public async Task SnoopActivity_ShouldPassWhisperDirection_WhenSet()
    {
        var channels = Substitute.For<IAriChannelsResource>();
        _ari.Channels.Returns(channels);

#pragma warning disable CA2012
        channels.SnoopAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AriChannel>(new AriChannel { Id = "s1", Name = "Snoop/s1" }));
#pragma warning restore CA2012

        var activity = new SnoopActivity(_ari)
        {
            TargetChannelId = "target-001",
            App = "coaching",
            Spy = SnoopDirection.Out,
            Whisper = SnoopDirection.In,
            SnoopId = "custom-snoop-id",
        };

        await activity.StartAsync();

        await channels.Received(1).SnoopAsync(
            "target-001",
            "coaching",
            "out",
            "in",
            "custom-snoop-id",
            Arg.Any<CancellationToken>());
    }
}
