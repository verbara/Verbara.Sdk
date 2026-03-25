using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketClientTests
{
    [Fact]
    public async Task SendAudioAsync_ShouldThrow_WhenNotConnected()
    {
        var client = new AudioSocketClient("127.0.0.1", 9999, Guid.NewGuid());

        var act = () => client.SendAudioAsync(new byte[320]).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not connected*");
    }

    [Fact]
    public async Task SendHangupAsync_ShouldThrow_WhenNotConnected()
    {
        var client = new AudioSocketClient("127.0.0.1", 9999, Guid.NewGuid());

        var act = () => client.SendHangupAsync().AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not connected*");
    }

    [Fact]
    public async Task ReadAudioAsync_ShouldThrow_WhenNotConnected()
    {
        var client = new AudioSocketClient("127.0.0.1", 9999, Guid.NewGuid());

        Func<Task> act = async () =>
        {
            await foreach (var _ in client.ReadAudioAsync())
            {
                // should not reach here
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not connected*");
    }

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        var client = new AudioSocketClient("127.0.0.1", 9999, Guid.NewGuid());

        // Dispose without ever connecting should not throw
        Func<Task> act = async () =>
        {
            await client.DisposeAsync();
            await client.DisposeAsync();
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ConnectAndDispose_ShouldWorkEndToEnd()
    {
        var options = new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(5) };
        await using var server = new AudioSocketServer(options,
            NullLogger<AudioSocketServer>.Instance);
        await server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, channelId);
        await client.ConnectAsync();

        // After connect, send should not throw
        Func<Task> act = async () =>
        {
            await client.SendAudioAsync(new byte[160]);
            await client.SendHangupAsync();
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Server_MaxConcurrentSessions_ShouldRejectExcess()
    {
        var options = new AudioSocketOptions
        {
            Port = 0,
            MaxConcurrentSessions = 1,
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };
        await using var server = new AudioSocketServer(options,
            NullLogger<AudioSocketServer>.Instance);

        var sessions = new List<AudioSocketSession>();
        server.OnSessionStarted += session =>
        {
            sessions.Add(session);
            return ValueTask.CompletedTask;
        };

        await server.StartAsync(CancellationToken.None);

        // First connection should succeed
        await using var client1 = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client1.ConnectAsync();
        await Task.Delay(200);

        sessions.Should().HaveCount(1);
        server.ActiveSessionCount.Should().Be(1);

        // Second connection should be rejected (session limit = 1)
        await using var client2 = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client2.ConnectAsync();
        await Task.Delay(300);

        // Session limit reached: second session should not be tracked
        server.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public async Task Server_StopAsync_ShouldCleanUpAllSessions()
    {
        var options = new AudioSocketOptions
        {
            Port = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };
        await using var server = new AudioSocketServer(options,
            NullLogger<AudioSocketServer>.Instance);
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync();
        await Task.Delay(200);

        server.ActiveSessionCount.Should().Be(1);

        await server.StopAsync(CancellationToken.None);

        server.ActiveSessionCount.Should().Be(0);
    }
}
