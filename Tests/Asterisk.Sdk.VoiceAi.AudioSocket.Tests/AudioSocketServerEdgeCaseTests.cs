using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketServerEdgeCaseTests : IAsyncDisposable
{
    private readonly AudioSocketServer _server;

    public AudioSocketServerEdgeCaseTests()
    {
        var options = new AudioSocketOptions
        {
            Port = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(1),
            MaxConcurrentSessions = 2
        };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    [Fact]
    public async Task Server_ShouldRejectConnection_WhenNoUuidSentWithinTimeout()
    {
        await _server.StartAsync(CancellationToken.None);

        // Connect a raw TCP client that does NOT send a UUID frame
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync("127.0.0.1", _server.BoundPort);

        // Wait longer than the timeout
        await Task.Delay(1500);

        // The server should have closed the connection
        _server.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public async Task Server_ShouldRejectConnection_WhenMaxSessionsReached()
    {
        await _server.StartAsync(CancellationToken.None);

        // Fill up to max (2 sessions)
        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await using var client2 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client1.ConnectAsync();
        await client2.ConnectAsync();
        await Task.Delay(300);

        _server.ActiveSessionCount.Should().Be(2);

        // Third connection should be rejected
        await using var client3 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client3.ConnectAsync();
        await Task.Delay(300);

        // Session count should not exceed max
        _server.ActiveSessionCount.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Server_BoundPort_ShouldBeZero_BeforeStart()
    {
        var options = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);

        server.BoundPort.Should().Be(0);
    }

    [Fact]
    public async Task Server_StopAsync_ShouldCleanUpAllSessions()
    {
        await _server.StartAsync(CancellationToken.None);

        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client1.ConnectAsync();
        await Task.Delay(200);

        _server.ActiveSessionCount.Should().Be(1);

        await _server.StopAsync(CancellationToken.None);

        _server.ActiveSessionCount.Should().Be(0);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }
}
