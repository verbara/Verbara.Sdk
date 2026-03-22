namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.NetworkPartition;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class LatencyTests : FunctionalTestBase
{
    private const string ProxyName = ToxiproxyFixture.AmiProxyName;

    [ToxiproxyFact]
    public async Task HighLatency_ShouldTriggerHeartbeatTimeout()
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
            await ToxiproxyControl.AddToxicAsync(ProxyName, "high-latency", "latency", "downstream",
                new Dictionary<string, object> { ["latency"] = 5000, ["jitter"] = 0 });

            // Wait for heartbeat to detect the latency and trigger disconnect
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (connection.State == AmiConnectionState.Connected && !cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            connection.State.Should().NotBe(AmiConnectionState.Connected,
                "high latency (5s) exceeding heartbeat timeout (3s) should cause disconnect");
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "high-latency"); } catch { }
        }
    }

    [ToxiproxyFact]
    public async Task ModerateLatency_ShouldNotDisconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            await ToxiproxyControl.AddToxicAsync(ProxyName, "moderate-latency", "latency", "downstream",
                new Dictionary<string, object> { ["latency"] = 500, ["jitter"] = 0 });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await connection.SendActionAsync(new PingAction(), cts.Token);

            response.Response.Should().Be("Success");
            connection.State.Should().Be(AmiConnectionState.Connected,
                "moderate latency (500ms) should not exceed heartbeat timeout (10s)");
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "moderate-latency"); } catch { }
        }
    }

    [ToxiproxyFact]
    public async Task LatencySpike_ShouldRecoverAfterRestore()
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
            // Inject extreme latency to force disconnect
            await ToxiproxyControl.AddToxicAsync(ProxyName, "latency-spike", "latency", "downstream",
                new Dictionary<string, object> { ["latency"] = 10000, ["jitter"] = 0 });

            // Wait for connection to detect the issue
            using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (connection.State == AmiConnectionState.Connected && !disconnectCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), disconnectCts.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            connection.State.Should().NotBe(AmiConnectionState.Connected);

            // Remove the toxic so reconnection can succeed
            await ToxiproxyControl.RemoveToxicAsync(ProxyName, "latency-spike");

            // Wait for reconnection
            using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            reconnectCts.Token.Register(() => reconnected.TrySetCanceled());
            await reconnected.Task;

            connection.State.Should().Be(AmiConnectionState.Connected);

            // Verify the connection is functional
            var response = await connection.SendActionAsync(new PingAction());
            response.Response.Should().Be("Success");
        }
        finally
        {
            try { await ToxiproxyControl.RemoveToxicAsync(ProxyName, "latency-spike"); } catch { }
        }
    }
}
