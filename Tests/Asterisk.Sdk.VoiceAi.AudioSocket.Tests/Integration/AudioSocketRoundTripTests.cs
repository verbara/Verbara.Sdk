using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests.Integration;

/// <summary>Integration tests: AudioSocketClient - AudioSocketServer round-trip.</summary>
public sealed class AudioSocketRoundTripTests : IAsyncDisposable
{
    private readonly AudioSocketServer _server;

    public AudioSocketRoundTripTests()
    {
        var options = new AudioSocketOptions
        {
            Port = 0, // ephemeral port
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    [Fact]
    public async Task RoundTrip_ShouldEchoAudio_WhenServerEchosBack()
    {
        // Server echoes received audio back to client
        _server.OnSessionStarted += async session =>
        {
            await foreach (var chunk in session.ReadAudioAsync())
            {
                await session.WriteAudioAsync(chunk);
            }
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();

        byte[] sent = new byte[320];
        for (int i = 0; i < sent.Length; i++) sent[i] = (byte)(i % 256);
        await client.SendAudioAsync(sent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ReadOnlyMemory<byte>? received = null;

        await foreach (var chunk in client.ReadAudioAsync(cts.Token))
        {
            received = chunk;
            break;
        }

        received.Should().NotBeNull();
        received!.Value.ToArray().Should().BeEquivalentTo(sent);
    }

    [Fact]
    public async Task RoundTrip_ShouldHandleMultipleConcurrentSessions()
    {
        // 3 concurrent sessions, each sends and receives its own audio
        var sessionAudio = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]>();

        _server.OnSessionStarted += async session =>
        {
            await foreach (var chunk in session.ReadAudioAsync())
            {
                await session.WriteAudioAsync(chunk);
            }
        };

        await _server.StartAsync(CancellationToken.None);

        var tasks = Enumerable.Range(0, 3).Select(async i =>
        {
            var channelId = Guid.NewGuid();
            await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
            await client.ConnectAsync();

            byte[] data = new byte[160];
            data[0] = (byte)i; // differentiate sessions
            await client.SendAudioAsync(data);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var chunk in client.ReadAudioAsync(cts.Token))
            {
                sessionAudio[channelId] = chunk.ToArray();
                break;
            }
        });

        await Task.WhenAll(tasks);

        sessionAudio.Should().HaveCount(3);
        // Each session should have received its own data
        foreach (var (_, audio) in sessionAudio)
            audio.Should().HaveCount(160);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _server.DisposeAsync();
    }
}
