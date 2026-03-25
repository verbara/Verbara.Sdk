using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketSessionTests : IAsyncDisposable
{
    private readonly AudioSocketServer _server;

    public AudioSocketSessionTests()
    {
        var options = new AudioSocketOptions
        {
            Port = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    private async Task<(AudioSocketSession session, AudioSocketClient client)> CreateSessionAsync()
    {
        var tcs = new TaskCompletionSource<AudioSocketSession>();
        _server.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        return (session, client);
    }

    // ── WriteSilenceAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task WriteSilenceAsync_ShouldSendSilenceFrame()
    {
        var (session, client) = await CreateSessionAsync();

        // WriteSilenceAsync sends a Silence frame with 2-byte payload [0, 0]
        await session.WriteSilenceAsync();

        // The client should be able to read back the silence frame as raw data.
        // Since AudioSocketClient.ReadAudioAsync only yields Audio frames,
        // we verify the session didn't throw and is still connected.
        session.IsConnected.Should().BeTrue();

        await client.DisposeAsync();
    }

    // ── HangupAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HangupAsync_ShouldSendHangupFrameAndDispose()
    {
        var (session, client) = await CreateSessionAsync();

        await session.HangupAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task HangupAsync_ShouldThrow_WhenAlreadyDisposed()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        var act = async () => await session.HangupAsync();

        await act.Should().ThrowAsync<ObjectDisposedException>();

        await client.DisposeAsync();
    }

    // ── FireHangup (concurrent safety) ───────────────────────────────────────

    [Fact]
    public async Task OnHangup_ShouldFireOnlyOnce_WhenClientSendsHangup()
    {
        var (session, client) = await CreateSessionAsync();

        int hangupCount = 0;
        session.OnHangup += () => Interlocked.Increment(ref hangupCount);

        await client.SendHangupAsync();
        await Task.Delay(500); // give time for read loop to process

        hangupCount.Should().Be(1);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task OnHangup_ShouldFireOnlyOnce_WhenDisposedAfterHangup()
    {
        var (session, client) = await CreateSessionAsync();

        int hangupCount = 0;
        session.OnHangup += () => Interlocked.Increment(ref hangupCount);

        await client.SendHangupAsync();
        await Task.Delay(500);

        // Disposing again should not fire hangup a second time
        await session.DisposeAsync();
        await Task.Delay(100);

        hangupCount.Should().Be(1);

        await client.DisposeAsync();
    }

    // ── WriteAudioAsync with different frame types ───────────────────────────

    [Fact]
    public async Task WriteAudioAsync_ShouldSendDefaultAudioFrame()
    {
        var (session, client) = await CreateSessionAsync();

        byte[] audio = new byte[160];
        Random.Shared.NextBytes(audio);
        await session.WriteAudioAsync(audio);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReadOnlyMemory<byte>? received = null;
        await foreach (var chunk in client.ReadAudioAsync(cts.Token))
        {
            received = chunk;
            break;
        }

        received.Should().NotBeNull();
        received!.Value.ToArray().Should().BeEquivalentTo(audio);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task WriteAudioAsync_WithFrameType_ShouldNotThrow()
    {
        var (session, client) = await CreateSessionAsync();

        byte[] audio = new byte[320];
        Random.Shared.NextBytes(audio);

        // Write with explicit Slin16 frame type
        await session.WriteAudioAsync(audio, AudioSocketFrameType.AudioSlin16);

        // Session should still be connected after writing
        session.IsConnected.Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task WriteAudioAsync_ShouldThrow_WhenDisposed()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        byte[] audio = new byte[160];
        var act = async () => await session.WriteAudioAsync(audio);

        await act.Should().ThrowAsync<ObjectDisposedException>();

        await client.DisposeAsync();
    }

    // ── IsConnected state transitions ────────────────────────────────────────

    [Fact]
    public async Task IsConnected_ShouldBeTrue_AfterSessionCreated()
    {
        var (session, client) = await CreateSessionAsync();

        session.IsConnected.Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task IsConnected_ShouldBeFalse_AfterDispose()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task IsConnected_ShouldBeFalse_AfterHangup()
    {
        var (session, client) = await CreateSessionAsync();

        await session.HangupAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    // ── ChannelId and RemoteEndpoint ─────────────────────────────────────────

    [Fact]
    public async Task ChannelId_ShouldMatchClientChannelId()
    {
        var tcs = new TaskCompletionSource<AudioSocketSession>();
        _server.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var expectedId = Guid.NewGuid();
        var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, expectedId);
        await client.ConnectAsync();

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        session.ChannelId.Should().Be(expectedId);
        session.RemoteEndpoint.Should().NotBeNullOrEmpty();

        await client.DisposeAsync();
    }

    // ── DisposeAsync idempotency ─────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        var (session, client) = await CreateSessionAsync();

        // Calling DisposeAsync multiple times should not throw
        await session.DisposeAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    // ── WriteSilenceAsync throws when disposed ───────────────────────────────

    [Fact]
    public async Task WriteSilenceAsync_ShouldThrow_WhenDisposed()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        var act = async () => await session.WriteSilenceAsync();

        await act.Should().ThrowAsync<ObjectDisposedException>();

        await client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _server.DisposeAsync();
    }
}
