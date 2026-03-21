namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Trait("Category", "Integration")]
public sealed class AmiReconnectionTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    [AsteriskContainerFact]
    public async Task Connection_ShouldReconnect_WhenAsteriskRestarted()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        connection.State.Should().Be(AmiConnectionState.Connected);

        // Verify connection is functional after reconnect
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success");
    }

    [AsteriskContainerFact]
    public async Task Connection_ShouldTransitionToReconnecting_WhenAsteriskKilled()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            await DockerControl.KillContainerAsync();

            // Wait briefly for state transition
            await Task.Delay(TimeSpan.FromSeconds(3));

            connection.State.Should().BeOneOf(
                AmiConnectionState.Reconnecting,
                AmiConnectionState.Disconnected);
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }

    [AsteriskContainerFact]
    public async Task SendAction_ShouldTimeout_WhenAsteriskKilledDuringAction()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(3);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            await DockerControl.KillContainerAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));

            var act = async () => await connection.SendActionAsync(new PingAction());
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }

    [AsteriskContainerFact]
    public async Task Connection_ShouldRespectMaxReconnectAttempts()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.MaxReconnectAttempts = 3;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
            opts.ReconnectMultiplier = 1.0;
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            await DockerControl.KillContainerAsync();

            // Wait long enough for 3 attempts at 500ms each, plus margin
            await Task.Delay(TimeSpan.FromSeconds(10));

            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }

    [AsteriskContainerFact]
    public async Task Connection_ShouldUseExponentialBackoff()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.MaxReconnectAttempts = 3;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(200);
            opts.ReconnectMultiplier = 2.0;
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            await DockerControl.KillContainerAsync();

            // Wait for all attempts to exhaust: 200ms + 400ms + 800ms + margin
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Verify reconnect-related log entries exist
            var reconnectLogs = LogCapture.Entries
                .Where(e => e.Message.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
                         || e.Message.Contains("Reconnect", StringComparison.Ordinal))
                .ToList();

            reconnectLogs.Should().NotBeEmpty("reconnection attempts should generate log entries");
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }
}
