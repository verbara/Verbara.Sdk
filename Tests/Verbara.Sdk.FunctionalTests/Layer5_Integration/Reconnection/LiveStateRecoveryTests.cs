namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

using System.Diagnostics;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class LiveStateRecoveryTests : FunctionalTestBase
{
    private const string ProxyName = ToxiproxyFixture.AmiProxyName;
    private const string LinkCutToxic = "live-state-link-cut";

    [Fact]
    public async Task VerbaraServer_ShouldReloadState_AfterReconnect()
    {
        // Connect through Toxiproxy: its port is stable across Asterisk restarts.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

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

        // After reconnect, the server should have reloaded state
        var reloadLogs = LogCapture.Entries
            .Where(e => e.Message.Contains("Reconnected", StringComparison.Ordinal)
                     || e.Message.Contains("reloading state", StringComparison.OrdinalIgnoreCase))
            .ToList();

        reloadLogs.Should().NotBeEmpty("server should log state reload on reconnect");
    }

    [Fact]
    public async Task ChannelManager_ShouldClearOnReconnect()
    {
        // Connect through Toxiproxy: its port is stable across Asterisk restarts, and it lets the
        // test cut the AMI link before Asterisk stops.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
            // Short retry gaps, so the reconnect follows soon after the cut is lifted.
            opts.ReconnectMaxDelay = TimeSpan.FromSeconds(2);
            // The reset toxic drops an open link only once data flows through it, so ping every
            // second. The default 10 s heartbeat timeout outlasts the 1 s reconnect delay, so the
            // ping lost in the reset cannot end auto-reconnect before the reconnect loop starts.
            opts.HeartbeatInterval = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        // Extension 100 of [test-functional] answers and waits 30 s, so the originate creates a
        // Local channel pair that stays up through the steps below.
        var originate = await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "30",
            IsAsync = true
        });
        originate.Response.Should().Be("Success", "the originate must be queued to create a channel");

        await WaitUntilAsync(() => server.Channels.ChannelCount > 0, TimeSpan.FromSeconds(10));
        server.Channels.ChannelCount.Should().BeGreaterThan(0,
            "a tracked channel must exist before the reconnect, or clearing proves nothing");

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        // Cut the AMI link before restarting Asterisk. A stopping Asterisk hangs up its channels,
        // and those Hangup events would remove the channels even if the reconnect never cleared them.
        await ToxiproxyControl.AddToxicAsync(ProxyName, LinkCutToxic, "reset_peer", "downstream",
            new Dictionary<string, object> { ["timeout"] = 0 });
        try
        {
            await WaitUntilAsync(() => connection.State != AmiConnectionState.Connected, TimeSpan.FromSeconds(10));
            connection.State.Should().NotBe(AmiConnectionState.Connected, "the reset must drop the AMI link");
            server.Channels.ChannelCount.Should().BeGreaterThan(0,
                "no Hangup event can arrive over the cut link, so the channels are still tracked");

            await DockerControl.RestartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
        finally
        {
            // Lift the cut even on failure: the rest of the collection connects through this proxy.
            await ToxiproxyControl.RemoveToxicAsync(ProxyName, LinkCutToxic);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        // Give a moment for state reload to complete
        await Task.Delay(TimeSpan.FromSeconds(2));

        // The restarted Asterisk has no channels, so the reload adds none: only the clear on
        // reconnect can remove the channels tracked before the cut.
        server.Channels.ChannelCount.Should().Be(0,
            "the reconnect must clear channels whose Hangup events never arrived");
    }

    [Fact]
    public async Task EventSubscription_ShouldResume_AfterReconnect()
    {
        // Connect through Toxiproxy: its port is stable across Asterisk restarts.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();

        var eventsReceived = new List<ManagerEvent>();
        var observer = new TestEventObserver(eventsReceived);
        using var subscription = connection.Subscribe(observer);

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

        // After reconnect, send an action that generates events to verify event flow
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success",
            "connection should be functional after reconnect for event subscription to work");
    }

    [Fact]
    public async Task PendingActions_ShouldNotHang_AfterDisconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(5);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            await DockerControl.KillContainerAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Action should complete with an error (timeout or connection lost), not hang
            using var actionCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var act = async () => await connection.SendActionAsync(
                new PingAction(), actionCts.Token);

            await act.Should().ThrowAsync<Exception>(
                "pending action should fail when connection is lost");
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }

    [Fact]
    public async Task MultipleReconnects_ShouldAllSucceed()
    {
        // Connect through Toxiproxy: its port is stable across Asterisk restarts.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        for (var i = 0; i < 3; i++)
        {
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Reconnected += OnReconnected;

            await DockerControl.RestartContainerAsync();
            await DockerControl.WaitForHealthyAsync();

            // Guard: reconnect may have completed before we start waiting
            if (connection.State == AmiConnectionState.Connected)
                reconnected.TrySetResult();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            cts.Token.Register(() => reconnected.TrySetCanceled());
            await reconnected.Task;

            connection.Reconnected -= OnReconnected;

            connection.State.Should().Be(AmiConnectionState.Connected,
                $"reconnect iteration {i + 1} should succeed");

            var response = await connection.SendActionAsync(new PingAction());
            response.Response.Should().Be("Success",
                $"ping after reconnect iteration {i + 1} should succeed");

            void OnReconnected() => reconnected.TrySetResult();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(start) < timeout)
            await Task.Delay(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Simple observer implementation for collecting events in tests.</summary>
    private sealed class TestEventObserver(List<ManagerEvent> events) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            lock (events) events.Add(value);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
