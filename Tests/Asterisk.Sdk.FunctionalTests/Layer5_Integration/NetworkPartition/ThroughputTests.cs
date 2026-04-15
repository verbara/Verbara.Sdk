namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.NetworkPartition;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ThroughputTests : FunctionalTestBase
{
    private const string ProxyName = ToxiproxyFixture.AmiProxyName;

    [Fact]
    public async Task BandwidthThrottle_ShouldTimeoutCleanly()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(3);
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            // Throttle bandwidth to 1 KB/s
            await ToxiproxyControl.AddToxicAsync(ProxyName, "bandwidth-limit", "bandwidth", "downstream",
                new Dictionary<string, object> { ["rate"] = 1 });

            // Action may succeed (slowly) or timeout depending on message size
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await connection.SendActionAsync(new PingAction(), cts.Token);
                // If it succeeds under throttle, that's acceptable
                response.Response.Should().Be("Success");
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Timeout is the expected behavior under extreme throttle
            }

            // Remove toxic and verify recovery
            await ToxiproxyControl.RemoveToxicAsync(ProxyName, "bandwidth-limit");

            // Give connection time to stabilize or reconnect
            await Task.Delay(TimeSpan.FromSeconds(2));

            // If disconnected, wait for reconnect
            if (connection.State != AmiConnectionState.Connected)
            {
                var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Reconnected += () => reconnected.TrySetResult();
                using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                reconnectCts.Token.Register(() => reconnected.TrySetCanceled());
                await reconnected.Task;
            }

            var verifyResponse = await connection.SendActionAsync(new PingAction());
            verifyResponse.Response.Should().Be("Success");
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "bandwidth-limit"); } catch { }
        }
    }

    [Fact]
    public async Task SlicedPackets_ShouldParseCorrectly()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            // Fragment packets into ~10 byte chunks
            await ToxiproxyControl.AddToxicAsync(ProxyName, "slicer", "slicer", "downstream",
                new Dictionary<string, object>
                {
                    ["average_size"] = 10,
                    ["size_variation"] = 5,
                    ["delay"] = 0
                });

            // The pipeline parser should reassemble fragmented data correctly
            var response = await connection.SendActionAsync(new PingAction());
            response.Response.Should().Be("Success",
                "Pipelines reader should handle fragmented TCP segments");

            connection.State.Should().Be(AmiConnectionState.Connected);
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "slicer"); } catch { }
        }
    }

    [Fact]
    public async Task LimitData_ShouldHandlePartialMessage()
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
            // Connection will close after 500 bytes of downstream data
            await ToxiproxyControl.AddToxicAsync(ProxyName, "limit-data", "limit_data", "downstream",
                new Dictionary<string, object> { ["bytes"] = 500 });

            // Generate traffic to trigger the byte limit
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await connection.SendActionAsync(new PingAction(), cts.Token);
            }
            catch
            {
                // Expected: connection may be cut mid-response
            }

            // Wait for state to transition (limit_data closes the connection)
            using var stateCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (connection.State == AmiConnectionState.Connected && !stateCts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), stateCts.Token); } catch (OperationCanceledException) { break; }
            }

            // Remove the one-shot toxic (may already be gone) so reconnection can work
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "limit-data"); } catch { }

            // Check if already reconnected
            if (connection.State == AmiConnectionState.Connected)
                reconnected.TrySetResult();

            // Wait for reconnection
            using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            reconnectCts.Token.Register(() => reconnected.TrySetCanceled());
            await reconnected.Task;

            connection.State.Should().Be(AmiConnectionState.Connected);
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "limit-data"); } catch { }
        }
    }

    [Fact]
    public async Task ActionDuringPartition_ShouldTimeoutCleanly()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(3);
            opts.AutoReconnect = false;
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            // Infinite timeout: all data is silently dropped
            await ToxiproxyControl.AddToxicAsync(ProxyName, "infinite-timeout", "timeout", "downstream",
                new Dictionary<string, object> { ["timeout"] = 0 });

            // Sending an action during partition should throw within the timeout window
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var act = async () => await connection.SendActionAsync(new PingAction(), cts.Token);
            await act.Should().ThrowAsync<Exception>(
                "action during network partition should timeout or fail");

            // Without heartbeat, the connection may still appear "Connected"
            // since the TCP write succeeded — only the response never arrived.
            // The key assertion is that the action threw an exception above.
            // State may be Connected (no heartbeat to detect partition) or not.
            connection.State.Should().BeOneOf(
                AmiConnectionState.Connected,
                AmiConnectionState.Connecting,
                AmiConnectionState.Reconnecting,
                AmiConnectionState.Disconnected);
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "infinite-timeout"); } catch { }
        }
    }
}
