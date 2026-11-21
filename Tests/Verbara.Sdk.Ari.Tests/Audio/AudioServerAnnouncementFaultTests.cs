using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// What an <c>OnStreamConnected</c> observer that throws may stop on each real audio server: on a stream, at the
/// disposal's completion, and when the host's logger fails on the report too. The announced connection, the later
/// observers and the disposal go on; the failure is counted once on <c>audio.stream_connected.subscriber_faults</c>,
/// tagged with the server's transport, and logged once at Warning.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs on a real server over a real loopback socket. No wait is a delay: every wait ends on the signal it
/// asserts (an observer's call, a frame read from the stream) and is bounded only so that a red test ends.
/// </para>
/// <para>
/// The instruments are process-wide, so the class runs in <see cref="AudioStreamMetricsGroup"/>, alone.
/// </para>
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class AudioServerAnnouncementFaultTests
{
    public enum Transport
    {
        AudioSocket,
        WebSocket,
    }

    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // The published names: the contract an exporter sees. Literals on purpose, so a rename is a red here.
    private const string SubscriberFaults = "audio.stream_connected.subscriber_faults";
    private const string TransportTag = "audio.transport";

    private const int AudioPayloadLength = 320;

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldAnnounceToTheLaterObserverKeepTheStreamAndReportOnce_WhenTheFirstObserverThrows(
        Transport transport)
    {
        // Arrange — two observers in subscription order, the first throwing on every stream
        using var recorder = new AudioMeasurementRecorder(SubscriberFaults);
        await using var door = await Door.StartAsync(transport);
        var later = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throwing = door.Server.OnStreamConnected.Subscribe(
            static _ => throw new InvalidOperationException("the first observer's own failure"));
        using var laterSubscription = door.Server.OnStreamConnected.Subscribe(stream => later.TrySetResult(stream));

        // Act — a stream connects
        using var call = await door.ConnectAsync();
        // A wait that ends at the bound fails the test here: the later observer never received the stream.
        var announced = await later.Task.WaitAsync(SignalTimeout);

        // Assert — the stream the first observer threw on is announced to the later one, and it stays open
        var audioFlows = await call.AudioFlowsAsync(announced);
        using (var scope = new AssertionScope())
        {
            scope.AddReportable("measurements", recorder.Describe);
            scope.AddReportable("log", door.Log.Describe);
            announced.ChannelId.Should().Be(call.Key, "an observer subscribed after the one that throws still receives that stream");
            door.Server.GetStream(call.Key).Should().BeSameAs(announced, "the connection stays registered");
            door.Server.ActiveStreamCount.Should().Be(1, "and keeps its place");
            audioFlows.Should().BeTrue("and stays open: its audio still reaches the stream");
            door.Log.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning
                    && e.EventName == "StreamConnectedObserverFailed"
                    && e.Exception!.Message == "the first observer's own failure"
                    && Equals(e.Value("ChannelId"), call.Key),
                "the failure is logged once at Warning, naming the stream");
            door.Log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error, "an observer's failure is not the connection's");
            recorder.Total(SubscriberFaults).Should().Be(1, "and counted once");
            recorder.Of(SubscriberFaults).Should().OnlyContain(
                m => Equals(m.Tag(TransportTag), door.TransportName), "tagged with the server's transport");
        }
    }

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldAnnounceToTheLaterObserverKeepTheStreamAndReportOnce_WhenTheFirstObserverThrowsObjectDisposedOnARunningServer(
        Transport transport)
    {
        // Arrange — the first observer throws ObjectDisposedException, the type each server's connection handler also
        // catches when its own disposal races an announcement; the server is running, so this one is the observer's
        using var recorder = new AudioMeasurementRecorder(SubscriberFaults);
        await using var door = await Door.StartAsync(transport);
        var later = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throwing = door.Server.OnStreamConnected.Subscribe(
            static _ => throw new ObjectDisposedException("the first observer's own disposed resource"));
        using var laterSubscription = door.Server.OnStreamConnected.Subscribe(stream => later.TrySetResult(stream));

        // Act — a stream connects
        using var call = await door.ConnectAsync();
        // A wait that ends at the bound fails the test here: the exception reached the connection's handler.
        var announced = await later.Task.WaitAsync(SignalTimeout);

        // Assert — the guard reports it as any other observer failure; the connection never sees it
        var audioFlows = await call.AudioFlowsAsync(announced);
        using (var scope = new AssertionScope())
        {
            scope.AddReportable("measurements", recorder.Describe);
            scope.AddReportable("log", door.Log.Describe);
            announced.ChannelId.Should().Be(call.Key, "an observer subscribed after the one that throws still receives that stream");
            door.Server.GetStream(call.Key).Should().BeSameAs(announced, "the connection stays registered");
            door.Server.ActiveStreamCount.Should().Be(1, "and keeps its place");
            audioFlows.Should().BeTrue("and stays open: its audio still reaches the stream");
            door.Log.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning
                    && e.EventName == "StreamConnectedObserverFailed"
                    && e.Exception is ObjectDisposedException
                    && Equals(e.Value("ChannelId"), call.Key),
                "the failure is logged once at Warning, naming the stream");
            door.Log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error, "an observer's failure is not the connection's");
            recorder.Total(SubscriberFaults).Should().Be(1, "and counted once");
        }
    }

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task DisposeAsync_ShouldCompleteTheLaterObserverAndReportOnce_WhenTheFirstObserverThrowsOnTheCompletion(
        Transport transport)
    {
        // Arrange — two observers, the first throwing on the completion the disposal raises
        using var recorder = new AudioMeasurementRecorder(SubscriberFaults);
        var door = await Door.StartAsync(transport);
        var laterCompleted = 0;
        using var throwing = door.Server.OnStreamConnected.Subscribe(
            static _ => { },
            static () => throw new InvalidOperationException("the first observer's completion failed"));
        using var laterSubscription = door.Server.OnStreamConnected.Subscribe(
            static _ => { },
            () => Interlocked.Increment(ref laterCompleted));

        // Act
        var dispose = async () => await door.DisposeAsync().AsTask().WaitAsync(SignalTimeout);

        // Assert
        await dispose.Should().NotThrowAsync("the disposal finishes whatever an observer throws on its completion");
        using (var scope = new AssertionScope())
        {
            scope.AddReportable("measurements", recorder.Describe);
            scope.AddReportable("log", door.Log.Describe);
            Volatile.Read(ref laterCompleted).Should().Be(1, "the later observer receives the completion once");
            door.Log.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning
                    && e.EventName == "StreamConnectedObserverFailedOnCompletion"
                    && e.Exception!.Message == "the first observer's completion failed",
                "the failure is logged once at Warning");
            recorder.Total(SubscriberFaults).Should().Be(1, "and counted once");
            recorder.Of(SubscriberFaults).Should().OnlyContain(
                m => Equals(m.Tag(TransportTag), door.TransportName), "tagged with the server's transport");
        }
    }

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldAnnounceToTheLaterObserverAndCountTheFault_WhenTheLoggerThrowsOnTheReportToo(
        Transport transport)
    {
        // Arrange — a logger that throws on every line at Warning or above, and a first observer that throws
        using var recorder = new AudioMeasurementRecorder(SubscriberFaults);
        await using var door = await Door.StartAsync(transport, throwFrom: LogLevel.Warning);
        var later = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throwing = door.Server.OnStreamConnected.Subscribe(
            static _ => throw new InvalidOperationException("the first observer's own failure"));
        using var laterSubscription = door.Server.OnStreamConnected.Subscribe(stream => later.TrySetResult(stream));

        // Act
        using var call = await door.ConnectAsync();
        // A wait that ends at the bound fails the test here: the logger's failure left the report.
        var announced = await later.Task.WaitAsync(SignalTimeout);

        // Assert — a failing report changes nothing: the later observer, the stream and the count are as without it
        var audioFlows = await call.AudioFlowsAsync(announced);
        using (var scope = new AssertionScope())
        {
            scope.AddReportable("measurements", recorder.Describe);
            scope.AddReportable("log", door.Log.Describe);
            door.Server.GetStream(call.Key).Should().BeSameAs(announced, "the connection stays registered");
            audioFlows.Should().BeTrue("and open");
            door.Log.Entries.Should().Contain(
                e => e.EventName == "StreamConnectedObserverFailed",
                "the premise: the report reached the logger, which threw on it");
            recorder.Total(SubscriberFaults).Should().Be(1, "the fault is counted before the line is attempted");
        }
    }

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldCountNothing_WhenNoObserverThrows(Transport transport)
    {
        // Arrange — the negative control: two observers, neither throws
        using var recorder = new AudioMeasurementRecorder(SubscriberFaults);
        var door = await Door.StartAsync(transport);
        var later = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = 0;
        using var first = door.Server.OnStreamConnected.Subscribe(static _ => { }, () => Interlocked.Increment(ref completions));
        using var second = door.Server.OnStreamConnected.Subscribe(
            stream => later.TrySetResult(stream), () => Interlocked.Increment(ref completions));

        // Act
        using (await door.ConnectAsync())
            await later.Task.WaitAsync(SignalTimeout);
        await door.DisposeAsync().AsTask().WaitAsync(SignalTimeout);

        // Assert
        using (var scope = new AssertionScope())
        {
            scope.AddReportable("measurements", recorder.Describe);
            scope.AddReportable("log", door.Log.Describe);
            Volatile.Read(ref completions).Should().Be(2, "each observer receives the completion");
            recorder.Of(SubscriberFaults).Should().BeEmpty("no observer failed");
            door.Log.Entries.Should().NotContain(
                e => e.EventName != null && e.EventName.StartsWith("StreamConnectedObserverFailed", StringComparison.Ordinal),
                "no observer failed (the far end's abrupt close may still log its own transport Warning)");
        }
    }

    /// <summary>One real server on loopback, with its capturing logger.</summary>
    private sealed class Door : IAsyncDisposable
    {
        private readonly IAsyncDisposable _owner;
        private int _disposed;

        private Door(Transport transport, IAudioServer server, IAsyncDisposable owner, ThrowingLog log, int port)
        {
            Transport = transport;
            Server = server;
            _owner = owner;
            Log = log;
            Port = port;
        }

        public Transport Transport { get; }

        public IAudioServer Server { get; }

        public ThrowingLog Log { get; }

        public int Port { get; }

        /// <summary>The value of the <c>audio.transport</c> tag this server records.</summary>
        public string TransportName => Transport == Transport.AudioSocket ? "audiosocket" : "websocket";

        public static async Task<Door> StartAsync(Transport transport, LogLevel? throwFrom = null)
        {
            var log = new ThrowingLog(throwFrom);
            if (transport == Transport.AudioSocket)
            {
                var (server, port) = await LoopbackServerBind.StartAsync(
                    p => new AudioSocketServer(Options(p, p), log.For<AudioSocketServer>()),
                    s => s.StartAsync());
                return new Door(transport, server, server, log, port);
            }

            // MediaStartTimeout zero: the connection is announced under its path segment at once, without a MEDIA_START.
            var (ws, wsPort) = await LoopbackServerBind.StartAsync(
                p => new WebSocketAudioServer(Options(p, p), log.For<WebSocketAudioServer>()),
                s => s.StartAsync());
            return new Door(transport, ws, ws, log, wsPort);
        }

        /// <summary>Connects and identifies (AudioSocket) or completes the upgrade (WebSocket).</summary>
        public async Task<Call> ConnectAsync()
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            if (Transport == Transport.AudioSocket)
            {
                var uuid = Guid.NewGuid();
                var client = new TcpClient { NoDelay = true };
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, Port, bound.Token);
                    await client.GetStream().WriteAsync(Call.Frame(AudioFrameType.Uuid, uuid.ToByteArray(bigEndian: true)), bound.Token);
                    return new Call(uuid.ToString(), client, null);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            var key = "ch-" + Guid.NewGuid().ToString("N");
            var peer = await RawWebSocketPeer.ConnectAsync(Port, bound.Token);
            try
            {
                await peer.UpgradeAsync(key, bound.Token);
                return new Call(key, null, peer);
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? _owner.DisposeAsync() : ValueTask.CompletedTask;

        private static AudioServerOptions Options(int audioSocketPort, int webSocketPort) => new()
        {
            ListenAddress = "127.0.0.1",
            AudioSocketPort = audioSocketPort,
            WebSocketPort = webSocketPort,
            DefaultFormat = "slin16",
            IdleTimeout = TimeSpan.FromSeconds(5),
            MediaStartTimeout = TimeSpan.Zero,
        };
    }

    /// <summary>One call's far end.</summary>
    private sealed class Call(string key, TcpClient? audioSocket, RawWebSocketPeer? webSocket) : IDisposable
    {
        /// <summary>The key the server registers the stream under.</summary>
        public string Key { get; } = key;

        /// <summary>Sends one audio frame from the far end and reads it from the server's stream.</summary>
        public async Task<bool> AudioFlowsAsync(IAudioStream stream)
        {
            var payload = new byte[AudioPayloadLength];
            payload.AsSpan().Fill(0x5a);
            try
            {
                if (audioSocket is not null)
                    await audioSocket.GetStream().WriteAsync(Frame(AudioFrameType.Audio, payload));
                else
                    await webSocket!.SendBinaryAsync(payload);

                var frame = await stream.ReadFrameAsync().AsTask().WaitAsync(SignalTimeout);
                return frame.Length == AudioPayloadLength;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            audioSocket?.Dispose();
            webSocket?.Dispose();
        }

        public static byte[] Frame(AudioFrameType type, byte[] payload)
        {
            var frame = new byte[3 + payload.Length];
            frame[0] = (byte)type;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)payload.Length;
            payload.CopyTo(frame.AsSpan(3));
            return frame;
        }
    }

    /// <summary>
    /// Records every entry, then throws on each one at or above <c>throwFrom</c> when set: a host logger whose
    /// provider fails.
    /// </summary>
    private sealed class ThrowingLog(LogLevel? throwFrom)
    {
        private readonly Lock _gate = new();
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_gate)
                    return [.. _entries];
            }
        }

        public ILogger<T> For<T>() => new Typed<T>(this);

        public string Describe() =>
            string.Join(" | ", Entries.Select(e => $"{e.Level} {e.EventName}: {e.Exception?.Message}"));

        private void Write(CapturedLogEntry entry)
        {
            lock (_gate)
                _entries.Add(entry);
            if (throwFrom is { } level && entry.Level >= level)
                throw new InvalidOperationException("the host's logging provider failed");
        }

        private sealed class Typed<T>(ThrowingLog owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                IReadOnlyList<KeyValuePair<string, object?>> values =
                    state is IReadOnlyList<KeyValuePair<string, object?>> pairs ? [.. pairs] : [];
                owner.Write(new CapturedLogEntry(logLevel, eventId, formatter(state, exception), exception, values));
            }
        }
    }
}
