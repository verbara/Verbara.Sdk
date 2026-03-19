using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketServerTests : IAsyncDisposable
{
    private readonly AudioSocketServer _server;

    public AudioSocketServerTests()
    {
        // Port = 0: OS assigns an available ephemeral port, eliminating collision risk
        var options = new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(5) };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    [Fact]
    public async Task Server_ShouldAcceptConnection_AndFireOnSessionStarted()
    {
        AudioSocketSession? receivedSession = null;
        _server.OnSessionStarted += session =>
        {
            receivedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();

        // Give server time to process the UUID frame
        await Task.Delay(200);

        receivedSession.Should().NotBeNull();
        receivedSession!.ChannelId.Should().Be(channelId);
    }

    [Fact]
    public async Task Session_ReadAudioAsync_ShouldYieldAudioFrames()
    {
        AudioSocketSession? capturedSession = null;
        _server.OnSessionStarted += session =>
        {
            capturedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();
        await Task.Delay(200);

        capturedSession.Should().NotBeNull();

        byte[] audioData = new byte[320];
        Random.Shared.NextBytes(audioData);
        await client.SendAudioAsync(audioData);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReadOnlyMemory<byte>? received = null;

        await foreach (var chunk in capturedSession!.ReadAudioAsync(cts.Token))
        {
            received = chunk;
            break;
        }

        received.Should().NotBeNull();
        received!.Value.Length.Should().Be(320);
        received!.Value.ToArray().Should().BeEquivalentTo(audioData);
    }

    [Fact]
    public async Task Session_ShouldDetectHangup_WhenClientSendsHangupFrame()
    {
        AudioSocketSession? capturedSession = null;
        _server.OnSessionStarted += session =>
        {
            capturedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();
        await Task.Delay(200);

        bool hangupFired = false;
        capturedSession!.OnHangup += () => hangupFired = true;

        await client.SendHangupAsync();
        await Task.Delay(300);

        hangupFired.Should().BeTrue();
    }

    [Fact]
    public async Task Server_ActiveSessionCount_ShouldTrackConnections()
    {
        await _server.StartAsync(CancellationToken.None);

        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await using var client2 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());

        await client1.ConnectAsync();
        await client2.ConnectAsync();
        await Task.Delay(300);

        _server.ActiveSessionCount.Should().Be(2);
    }

    [Fact]
    public async Task Session_WriteAudioAsync_ShouldSendFramedData_ToClient()
    {
        AudioSocketSession? capturedSession = null;
        _server.OnSessionStarted += session =>
        {
            capturedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();
        await Task.Delay(200);

        byte[] responseAudio = new byte[160];
        for (int i = 0; i < responseAudio.Length; i++) responseAudio[i] = (byte)(i % 256);

        await capturedSession!.WriteAudioAsync(responseAudio);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReadOnlyMemory<byte>? received = null;
        await foreach (var chunk in client.ReadAudioAsync(cts.Token))
        {
            received = chunk;
            break;
        }

        received.Should().NotBeNull();
        received!.Value.ToArray().Should().BeEquivalentTo(responseAudio);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _server.DisposeAsync();
    }
}
