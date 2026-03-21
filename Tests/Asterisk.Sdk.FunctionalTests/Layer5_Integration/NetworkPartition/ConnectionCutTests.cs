namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.NetworkPartition;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Trait("Category", "Integration")]
public sealed class ConnectionCutTests : FunctionalTestBase, IClassFixture<ToxiproxyFixture>
{
    private const string ProxyName = ToxiproxyFixture.AmiProxyName;

    [ToxiproxyFact]
    public async Task TcpReset_ShouldTriggerReconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        try
        {
            // Send TCP RST to kill the connection
            await ToxiproxyControl.AddToxicAsync(ProxyName, "tcp-reset", "reset_peer", "downstream",
                new Dictionary<string, object> { ["timeout"] = 0 });

            // Wait for state to transition away from Connected
            using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (connection.State == AmiConnectionState.Connected && !disconnectCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), disconnectCts.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            connection.State.Should().NotBe(AmiConnectionState.Connected,
                "TCP RST should cause the connection to leave Connected state");

            // Remove the toxic so reconnection can succeed
            await ToxiproxyControl.RemoveToxicAsync(ProxyName, "tcp-reset");

            // Wait for reconnection
            using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            reconnectCts.Token.Register(() => reconnected.TrySetCanceled());
            await reconnected.Task;

            connection.State.Should().Be(AmiConnectionState.Connected);

            var response = await connection.SendActionAsync(new PingAction());
            response.Response.Should().Be("Success");
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "tcp-reset"); } catch { }
        }
    }

    [ToxiproxyFact]
    public async Task SilentDrop_ShouldDetectViaHeartbeat()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.HeartbeatInterval = TimeSpan.FromSeconds(2);
            opts.HeartbeatTimeout = TimeSpan.FromSeconds(3);
            opts.AutoReconnect = false;
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            // Silent partition: all data is stopped, no TCP RST
            await ToxiproxyControl.AddToxicAsync(ProxyName, "silent-drop", "timeout", "downstream",
                new Dictionary<string, object> { ["timeout"] = 0 });

            // Heartbeat should detect the dead connection within interval + timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (connection.State == AmiConnectionState.Connected && !cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            connection.State.Should().NotBe(AmiConnectionState.Connected,
                "heartbeat should detect silent partition and trigger disconnect");
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "silent-drop"); } catch { }
        }
    }

    [ToxiproxyFact]
    public async Task RestoreAfterPartition_ShouldReconnectCleanly()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.HeartbeatInterval = TimeSpan.FromSeconds(2);
            opts.HeartbeatTimeout = TimeSpan.FromSeconds(3);
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        try
        {
            // Create a silent partition
            await ToxiproxyControl.AddToxicAsync(ProxyName, "partition", "timeout", "downstream",
                new Dictionary<string, object> { ["timeout"] = 0 });

            // Wait for disconnect detection via heartbeat
            using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (connection.State == AmiConnectionState.Connected && !disconnectCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), disconnectCts.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            connection.State.Should().NotBe(AmiConnectionState.Connected);

            // Restore connectivity
            await ToxiproxyControl.RemoveToxicAsync(ProxyName, "partition");

            // Wait for successful reconnection
            using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            reconnectCts.Token.Register(() => reconnected.TrySetCanceled());
            await reconnected.Task;

            connection.State.Should().Be(AmiConnectionState.Connected);

            var response = await connection.SendActionAsync(new PingAction());
            response.Response.Should().Be("Success");
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "partition"); } catch { }
        }
    }
}
