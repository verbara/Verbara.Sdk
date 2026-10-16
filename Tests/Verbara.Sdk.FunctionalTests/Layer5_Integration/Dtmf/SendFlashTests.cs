namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Dtmf;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;

/// <summary>
/// <see cref="SendFlashAction"/> against a real Asterisk. The manager action is registered as <c>SendFlash</c>; any
/// other name is answered with <c>Response: Error</c> and <c>Message: Invalid/unknown command: ...</c> whatever the
/// channel. Measured on 22.10.1 and 23.4.1: a channel that does not exist is answered <c>Error</c> /
/// <c>Channel not found</c>, a live one <c>Success</c> / <c>Flash successfully queued</c>, followed by a
/// <c>Flash</c> event on the other half of the Local channel.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class SendFlashTests : FunctionalTestBase
{
    public SendFlashTests() : base("Verbara.Sdk.Ami")
    {
    }

    [Fact]
    public async Task SendFlash_ShouldBeAnsweredChannelNotFound_WhenChannelDoesNotExist()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var response = await connection.SendActionAsync(new SendFlashAction
        {
            Channel = "PJSIP/does-not-exist-0001"
        });

        using (new AssertionScope())
        {
            response.Message.Should().NotStartWith("Invalid/unknown command",
                "Asterisk must recognise the action the SDK sends");
            response.Response.Should().Be("Error");
            response.Message.Should().Be("Channel not found");
        }
    }

    [Fact]
    public async Task SendFlash_ShouldBeQueuedAndRaiseFlashEvent_WhenChannelIsUp()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var channelUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flashed = new TaskCompletionSource<FlashEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.Subscribe(new FlashObserver(
            onDialplanHalfUp: name => channelUp.TrySetResult(name),
            onFlash: e => flashed.TrySetResult(e)));

        // Ext 100 answers and waits 30 s; the ;2 half runs it.
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000
        });
        var channel = await channelUp.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var response = await connection.SendActionAsync(new SendFlashAction { Channel = channel });

        using (new AssertionScope())
        {
            response.Message.Should().NotStartWith("Invalid/unknown command",
                "Asterisk must recognise the action the SDK sends");
            response.Response.Should().Be("Success");
            response.Message.Should().Be("Flash successfully queued");
        }

        var flash = await flashed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        flash.Channel.Should().StartWith("Local/100@test-functional-", "the flash is raised on the Local channel pair");

        await connection.SendActionAsync(new HangupAction { Channel = channel });
    }

    private sealed class FlashObserver(Action<string> onDialplanHalfUp, Action<FlashEvent> onFlash)
        : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case FlashEvent flash:
                    onFlash(flash);
                    break;
                case NewStateEvent state when state.ChannelStateDesc == "Up"
                    && state.Channel is { } name && name.StartsWith("Local/100@test-functional-", StringComparison.Ordinal)
                    && name.EndsWith(";2", StringComparison.Ordinal):
                    onDialplanHalfUp(name);
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
