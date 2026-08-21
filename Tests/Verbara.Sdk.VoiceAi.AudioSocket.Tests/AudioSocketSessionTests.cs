using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

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

    // ── ReadAudioAsync teardown orderings (ADR-0053) ─────────────────────────

    /// <summary>
    /// Ordering (a): the hangup is fully processed before the consumer's first <c>MoveNextAsync</c>.
    /// Ordered by construction, not by delay — the client's read ends on EOF, and the FIN that
    /// produces it is emitted by <c>_client.Dispose()</c>, the last statement of the session's
    /// teardown. When the drain loop returns, the teardown has provably completed.
    /// </summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldDeliverBufferedAudioThenEnd_WhenTheHangupCompletesBeforeTheFirstRead()
    {
        var (session, client) = await CreateSessionAsync();
        var payload = new byte[160];
        payload[0] = 0x42;

        await client.SendAudioAsync(payload);   // lands in the 256-slot channel …
        await client.SendHangupAsync();         // … and the hangup follows it on the same stream

        await foreach (var _ in client.ReadAudioAsync(CancellationToken.None)) { /* drain to EOF */ }

        var frames = new List<ReadOnlyMemory<byte>>();
        await foreach (var frame in session.ReadAudioAsync())
            frames.Add(frame);

        frames.Should().ContainSingle("audio received before the hangup is still the caller's audio")
            .Which.Span[0].Should().Be(0x42);

        await client.DisposeAsync();
    }

    /// <summary>Ordering (b-variant): the owner disposes while a consumer is enumerating. A routine
    /// host shutdown must end the sequence, not fault it — see <c>AudioSocketServer.StopAsync</c>.</summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldEndTheSequence_WhenTheSessionIsDisposedMidEnumeration()
    {
        var (session, client) = await CreateSessionAsync();
        var firstFrameSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<ReadOnlyMemory<byte>>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var frame in session.ReadAudioAsync())
            {
                frames.Add(frame);
                firstFrameSeen.TrySetResult();
            }
        });

        await client.SendAudioAsync(new byte[160]);
        await firstFrameSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync();

        var fault = await Record.ExceptionAsync(() => consumer.WaitAsync(TimeSpan.FromSeconds(5)));

        fault.Should().BeNull("disposing the session is how a host shuts it down, not a failure");
        frames.Should().ContainSingle();

        await client.DisposeAsync();
    }

    /// <summary>Ordering (c) / R2: a read issued after the owner disposed throws from the call
    /// itself, naming the type — as every other member of this session already does.</summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldThrowObjectDisposedException_WhenTheOwnerDisposedTheSession()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        var act = () => session.ReadAudioAsync();

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Contain(nameof(AudioSocketSession));

        await client.DisposeAsync();
    }

    /// <summary>R1: the consumer's own cancellation still faults, and takes precedence over the
    /// sequence ending quietly. The cancelled token goes to the subject only (ADR-0052 F3).</summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldThrowOperationCanceled_WhenTheCallersTokenIsCancelled()
    {
        var (session, client) = await CreateSessionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var _ in session.ReadAudioAsync(cts.Token)) { /* never reached */ }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        await client.DisposeAsync();
    }

    /// <summary>Ordering (d): a bare EOF with no hangup frame. The session must release its
    /// transport — the server has already dropped it from its registry by the time OnHangup returns,
    /// so nothing else can ever reclaim it.</summary>
    [Fact]
    public async Task ReadLoop_ShouldReleaseTheTransport_WhenTheSocketEndsWithoutAHangupFrame()
    {
        var (session, client) = await CreateSessionAsync();
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnHangup += () => tornDown.TrySetResult();

        await client.DisposeAsync();   // FIN, no hangup frame

        await tornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.IsConnected.Should().BeFalse(
            "a session whose socket is gone is not connected, and its owner has already forgotten it");
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _server.DisposeAsync();
    }
}
