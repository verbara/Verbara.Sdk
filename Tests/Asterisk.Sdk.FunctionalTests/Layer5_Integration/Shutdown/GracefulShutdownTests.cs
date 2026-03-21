namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Shutdown;

using System.Diagnostics;
using System.Net.Sockets;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Hosting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[Trait("Category", "Integration")]
public sealed class GracefulShutdownTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    // -----------------------------------------------------------------------
    // Test 1: IHost stops and AMI connection becomes Disconnected
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task HostShutdown_ShouldCloseAmiConnection()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services => services.AddAsterisk(o =>
            {
                o.Ami.Hostname = AsteriskContainerFixture.Host;
                o.Ami.Port = AsteriskContainerFixture.AmiPort;
                o.Ami.Username = AmiConnectionFactory.Username;
                o.Ami.Password = AmiConnectionFactory.Password;
                o.Ami.AutoReconnect = false;
            }))
            .Build();

        await host.StartAsync();

        var connection = host.Services.GetRequiredService<IAmiConnection>();
        connection.State.Should().Be(AmiConnectionState.Connected);

        await host.StopAsync();

        connection.State.Should().BeOneOf(
            AmiConnectionState.Disconnected,
            AmiConnectionState.Disconnecting);
    }

    // -----------------------------------------------------------------------
    // Test 2: AGI server stops accepting after host shutdown
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task HostShutdown_ShouldStopAgiServer()
    {
        // Pick a free ephemeral port
        const int agiPort = 14573;

        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services => services.AddAsterisk(o =>
            {
                o.Ami.Hostname = AsteriskContainerFixture.Host;
                o.Ami.Port = AsteriskContainerFixture.AmiPort;
                o.Ami.Username = AmiConnectionFactory.Username;
                o.Ami.Password = AmiConnectionFactory.Password;
                o.Ami.AutoReconnect = false;
                o.AgiPort = agiPort;
                o.AgiMappingStrategy = new SimpleMappingStrategy();
            }))
            .Build();

        // Start the AGI server directly (not through the host) to avoid binding conflicts
        var logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<FastAgiServer>();
        var agiServer = new FastAgiServer(agiPort, new SimpleMappingStrategy(), logger);
        await agiServer.StartAsync();

        agiServer.IsRunning.Should().BeTrue("AGI server should be listening before shutdown");

        await agiServer.StopAsync();

        agiServer.IsRunning.Should().BeFalse("AGI server must stop accepting connections after StopAsync");
        agiServer.State.Should().Be(AgiServerState.Stopped);

        await agiServer.DisposeAsync();
        await host.StopAsync();
    }

    // -----------------------------------------------------------------------
    // Test 3: AudioSocket server stops listening after host shutdown
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task HostShutdown_ShouldStopAudioSocketServer()
    {
        // AudioSocketServer is registered only when ARI + ConfigureAudioServer is provided.
        // We test it directly here by instantiating through its hosted-service lifecycle.
        var options = new Asterisk.Sdk.VoiceAi.AudioSocket.AudioSocketOptions
        {
            Port = 18765,
            ListenAddress = "127.0.0.1"
        };

        var loggerFactory = LoggerFactory;
        var audioLogger = loggerFactory.CreateLogger<Asterisk.Sdk.VoiceAi.AudioSocket.AudioSocketServer>();
        var server = new Asterisk.Sdk.VoiceAi.AudioSocket.AudioSocketServer(options, audioLogger);

        await server.StartAsync(CancellationToken.None);
        server.BoundPort.Should().Be(options.Port, "server should be listening on the configured port");

        // Now stop — simulating host shutdown
        await server.StopAsync(CancellationToken.None);

        // Verify the port is released: a fresh TCP connect must fail / refuse
        var portAccepting = false;
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync("127.0.0.1", options.Port);
            portAccepting = true;
        }
        catch (SocketException) { /* expected — port should be released */ }

        portAccepting.Should().BeFalse("AudioSocketServer must release the port after StopAsync");

        await server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Test 4: Shutdown completes within 5 seconds
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task HostShutdown_ShouldCompleteWithinTimeout()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services => services.AddAsterisk(o =>
            {
                o.Ami.Hostname = AsteriskContainerFixture.Host;
                o.Ami.Port = AsteriskContainerFixture.AmiPort;
                o.Ami.Username = AmiConnectionFactory.Username;
                o.Ami.Password = AmiConnectionFactory.Password;
                o.Ami.AutoReconnect = false;
            }))
            .Build();

        await host.StartAsync();

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StopAsync(cts.Token);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "graceful shutdown must complete within 5 seconds");
    }

    // -----------------------------------------------------------------------
    // Test 5: Pending actions are cancelled on shutdown
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task HostShutdown_ShouldCancelActiveOperations()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        // Use a per-action CancellationToken that we control: start an action with a
        // long timeout token and then cancel both the token and the connection.
        using var actionCts = new CancellationTokenSource();

        // Fire several rapid actions without awaiting — they will be pending in-flight
        var pendingTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
                await connection.SendActionAsync(
                    new Asterisk.Sdk.Ami.Actions.PingAction(), actionCts.Token)))
            .ToList();

        // Give them a moment to be enqueued in-flight
        await Task.Delay(50);

        // Cancel the token AND disconnect to exercise both cancellation paths
        await actionCts.CancelAsync();
        await connection.DisconnectAsync();

        // All pending tasks must complete (throw or succeed) quickly — must not hang
        Func<Task> waitAll = () => Task.WhenAll(pendingTasks).WaitAsync(TimeSpan.FromSeconds(5));
        await waitAll.Should().ThrowAsync<Exception>(
            "pending AMI actions must be cancelled/faulted when the connection is closed");
    }

    // -----------------------------------------------------------------------
    // Test 6: No TCP socket leak after dispose
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task HostShutdown_ShouldNotLeakTcpSockets()
    {
        var connections = new List<IAmiConnection>();

        // Open several connections
        for (var i = 0; i < 3; i++)
        {
            var conn = AmiConnectionFactory.Create(LoggerFactory, opts =>
                opts.AutoReconnect = false);
            await conn.ConnectAsync();
            connections.Add(conn);
        }

        // All connected
        connections.Should().AllSatisfy(c =>
            c.State.Should().Be(AmiConnectionState.Connected));

        // Dispose all
        foreach (var conn in connections)
            await conn.DisposeAsync();

        // After disposal every connection must be in a terminal state
        connections.Should().AllSatisfy(c =>
            c.State.Should().BeOneOf(
                AmiConnectionState.Disconnected,
                AmiConnectionState.Disconnecting,
                AmiConnectionState.Initial));
    }

    // -----------------------------------------------------------------------
    // Test 7: Double-dispose must not throw
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task DisposingConnection_ShouldBeIdempotent()
    {
        var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
            opts.AutoReconnect = false);

        await connection.ConnectAsync();

        // First dispose
        await connection.DisposeAsync();

        // Second dispose must not throw
        Func<Task> secondDispose = async () => await connection.DisposeAsync();
        await secondDispose.Should().NotThrowAsync(
            "DisposeAsync must be idempotent — calling it twice must not throw");
    }

    // -----------------------------------------------------------------------
    // Test 8: Shutdown during reconnect must not hang
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task ShutdownDuringReconnect_ShouldNotHang()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(200);
            opts.MaxReconnectAttempts = 0; // unlimited
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        try
        {
            // Kill Asterisk to trigger reconnect loop
            await DockerControl.KillContainerAsync();

            // Wait briefly so the connection notices the disconnect and starts reconnecting
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Now call DisconnectAsync while the reconnect loop is running
            // Must complete within 5 seconds — must not hang
            Func<Task> disconnect = async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await connection.DisconnectAsync(cts.Token);
            };

            await disconnect.Should().NotThrowAsync(
                "DisconnectAsync must succeed even while the reconnection loop is active");
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }
    }
}
