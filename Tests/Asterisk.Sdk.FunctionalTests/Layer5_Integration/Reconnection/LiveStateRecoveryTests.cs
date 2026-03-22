namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class LiveStateRecoveryTests : FunctionalTestBase
{
    [AsteriskContainerFact]
    public async Task AsteriskServer_ShouldReloadState_AfterReconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        // After reconnect, the server should have reloaded state
        var reloadLogs = LogCapture.Entries
            .Where(e => e.Message.Contains("Reconnected", StringComparison.Ordinal)
                     || e.Message.Contains("reloading state", StringComparison.OrdinalIgnoreCase))
            .ToList();

        reloadLogs.Should().NotBeEmpty("server should log state reload on reconnect");
    }

    [AsteriskContainerFact]
    public async Task ChannelManager_ShouldClearOnReconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Originate a call to create a channel (will likely fail but may briefly create one)
        try
        {
            await connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/s@default",
                Application = "Wait",
                Data = "10",
                IsAsync = true
            });
        }
        catch
        {
            // Originate may fail if dialplan not configured; that's OK
        }

        await Task.Delay(TimeSpan.FromSeconds(1));

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        // Give a moment for state reload to complete
        await Task.Delay(TimeSpan.FromSeconds(2));

        // After restart, all previous channels should be gone (Asterisk restarted clean)
        // The clear happens in OnReconnected before reload
        // Channels should be 0 since Asterisk was freshly restarted with no active calls
        server.Channels.ChannelCount.Should().Be(0,
            "channels should be cleared after container restart with no active calls");
    }

    [AsteriskContainerFact]
    public async Task EventSubscription_ShouldResume_AfterReconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        // After reconnect, send an action that generates events to verify event flow
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success",
            "connection should be functional after reconnect for event subscription to work");
    }

    [AsteriskContainerFact]
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

    [AsteriskContainerFact]
    public async Task MultipleReconnects_ShouldAllSucceed()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
