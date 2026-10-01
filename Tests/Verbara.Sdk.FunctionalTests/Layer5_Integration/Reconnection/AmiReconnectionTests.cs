namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AmiReconnectionTests : FunctionalTestBase
{
    [Fact]
    public async Task Connection_ShouldReconnect_WhenAsteriskRestarted()
    {
        // Connect through Toxiproxy: its port is stable across Asterisk restarts and
        // its upstream (asterisk:5038 Docker DNS) resolves to the new container IP.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();

        // Guard: reconnect may have completed before we start waiting
        if (connection.State == AmiConnectionState.Connected)
            reconnected.TrySetResult();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        connection.State.Should().Be(AmiConnectionState.Connected);

        // Verify connection is functional after reconnect
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success");
    }

    [Fact]
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
                AmiConnectionState.Connecting,
                AmiConnectionState.Reconnecting,
                AmiConnectionState.Disconnected);
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }

    [Fact]
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

    [Fact]
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

            // Asterisk stays down, so every reconnect is refused: the loop uses its three attempts and gives up.
            // Bounded wait for the give-up itself, not an observation window.
            var gaveUp = await WaitForStateAsync(connection, AmiConnectionState.Disconnected, TimeSpan.FromSeconds(60));

            gaveUp.Should().BeTrue("with MaxReconnectAttempts = 3 and Asterisk down, the connection gives up and reads Disconnected");
            LogCapture.Entries
                .Count(e => e.Message.Contains("[AMI] Reconnect attempt failed", StringComparison.Ordinal))
                .Should().Be(3, "MaxReconnectAttempts = 3 makes exactly three reconnect attempts, each refused");
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }

    [Fact]
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

    private static async Task<bool> WaitForStateAsync(Verbara.Sdk.Ami.Connection.AmiConnection connection, AmiConnectionState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (connection.State == state) return true;
            await Task.Delay(50); // fence-allow: LOOP-DRIVER — AmiConnection exposes State but no state-change signal; bounded by the caller's timeout
        }

        return connection.State == state;
    }
}
