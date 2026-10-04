using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// Every state one consumer observer recorded for one stream, from the announcement to completion.
/// </summary>
internal sealed class StateRecording
{
    private readonly ConcurrentQueue<AudioStreamState> _states = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StateRecording(IAudioStream stream) => Stream = stream;

    public IAudioStream Stream { get; }

    /// <summary>What the observer was notified of, in order.</summary>
    public IReadOnlyList<AudioStreamState> States => [.. _states];

    /// <summary>Completes when the stream's state sequence completes.</summary>
    public Task Completed => _completed.Task;

    /// <summary>Completes on the first <see cref="AudioStreamState.Disconnected"/> the observer is notified of.</summary>
    public Task Ended => _ended.Task;

    /// <summary>Subscribes the recording observer to the stream.</summary>
    public IDisposable Subscribe() =>
        Stream.StateChanges.Subscribe(
            state =>
            {
                _states.Enqueue(state);
                if (state == AudioStreamState.Disconnected)
                    _ended.TrySetResult();
            },
            () => _completed.TrySetResult());
}

/// <summary>A log entry reduced to what the ending tests count.</summary>
internal sealed record CountedLogEntry(LogLevel Level, string? EventName, Exception? Exception, string Message);

/// <summary>
/// Records every entry, and completes a wait once a given number of matching entries has been written,
/// so a test ends on the count it asserts instead of on a delay.
/// </summary>
internal sealed class CountingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<CountedLogEntry> _entries = [];
    private readonly List<(Func<CountedLogEntry, bool> Match, int Count, TaskCompletionSource Signal)> _waiters = [];

    public IReadOnlyList<CountedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Completes once <paramref name="count"/> entries matching <paramref name="match"/> exist. The caller bounds the wait.</summary>
    public Task WhenCount(Func<CountedLogEntry, bool> match, int count)
    {
        lock (_gate)
        {
            if (_entries.Count(match) >= count)
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, count, signal));
            return signal.Task;
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var entry = new CountedLogEntry(logLevel, eventId.Name, exception, formatter(state, exception));
        List<TaskCompletionSource> met = [];
        lock (_gate)
        {
            _entries.Add(entry);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_entries.Count(_waiters[i].Match) < _waiters[i].Count)
                    continue;
                met.Add(_waiters[i].Signal);
                _waiters.RemoveAt(i);
            }
        }

        foreach (var signal in met)
            signal.TrySetResult();
    }
}

/// <summary>
/// A real ARI AudioSocket server on a loopback port, whose announcements are handed to a per-test
/// subscription action and recorded by channel id.
/// </summary>
internal sealed class AudioSocketEndingHarness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StateRecording>> _announced = new(StringComparer.Ordinal);
    private readonly IDisposable _subscription;
    private int _disposed;
    private int _announcements;

    /// <summary>How many streams the server has announced so far.</summary>
    public int Announcements => Volatile.Read(ref _announcements);

    private AudioSocketEndingHarness(AudioSocketServer server, int port, AudioServerOptions options, CountingLogger<AudioSocketServer> logger, Action<StateRecording> subscribe)
    {
        Server = server;
        Port = port;
        Options = options;
        Logger = logger;
        _subscription = server.OnStreamConnected.Subscribe(stream =>
        {
            Interlocked.Increment(ref _announcements);
            var recording = new StateRecording(stream);
            try
            {
                subscribe(recording);
            }
            finally
            {
                _announced.GetOrAdd(stream.ChannelId, NewSlot).TrySetResult(recording);
            }
        });
    }

    public AudioSocketServer Server { get; }

    public int Port { get; }

    public AudioServerOptions Options { get; }

    public CountingLogger<AudioSocketServer> Logger { get; }

    /// <summary>Starts a server on a loopback port; <paramref name="subscribe"/> runs inside each announcement.</summary>
    public static async Task<AudioSocketEndingHarness> StartAsync(
        Action<StateRecording> subscribe,
        int maxStreams = 1000,
        TimeSpan? idleTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var logger = new CountingLogger<AudioSocketServer>();
        AudioServerOptions? options = null;
        var (server, port) = await LoopbackServerBind.StartAsync(
            p =>
            {
                options = new AudioServerOptions
                {
                    AudioSocketPort = p,
                    ListenAddress = "127.0.0.1",
                    MaxConcurrentStreams = maxStreams,
                    DefaultFormat = "slin16",
                    IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(5),
                };
                return new AudioSocketServer(options, logger, timeProvider ?? TimeProvider.System);
            },
            s => s.StartAsync());
        return new AudioSocketEndingHarness(server, port, options!, logger, subscribe);
    }

    /// <summary>
    /// Starts a server bound to a port it never dials, whose accept seam hands it the connections this
    /// harness accepts on its own loopback listener, each wrapped so the test learns when the handler
    /// released it: <see cref="ReleaseSignallingClient.Released"/> fires at the handler's <c>using</c>,
    /// after it announced the stream or gave up on it.
    /// </summary>
    public static async Task<AudioSocketEndingHarness> StartWithReleaseSignalsAsync(Action<StateRecording> subscribe, int maxStreams = 1000)
    {
        var logger = new CountingLogger<AudioSocketServer>();
        var options = new AudioServerOptions
        {
            AudioSocketPort = 0,
            ListenAddress = "127.0.0.1",
            MaxConcurrentStreams = maxStreams,
            DefaultFormat = "slin16",
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new AudioSocketServer(options, logger, TimeProvider.System);
        var harness = new AudioSocketEndingHarness(server, ((IPEndPoint)listener.LocalEndpoint).Port, options, logger, subscribe)
        {
            _ownListener = listener,
        };
        server.AcceptOverride = async token =>
        {
            var accepted = new ReleaseSignallingClient(await listener.AcceptSocketAsync(token));
            harness._accepted.Writer.TryWrite(accepted);
            return accepted;
        };
        await server.StartAsync();
        return harness;
    }

    private readonly System.Threading.Channels.Channel<ReleaseSignallingClient> _accepted =
        System.Threading.Channels.Channel.CreateUnbounded<ReleaseSignallingClient>();

    private TcpListener? _ownListener;

    /// <summary>The next connection the accept seam handed to the server (<see cref="StartWithReleaseSignalsAsync"/> only).</summary>
    public async Task<ReleaseSignallingClient> NextAcceptedAsync(CancellationToken token) =>
        await _accepted.Reader.ReadAsync(token);

    /// <summary>The recording of the stream announced under <paramref name="id"/>, whenever it is announced.</summary>
    public Task<StateRecording> AnnouncedAs(string id) => _announced.GetOrAdd(id, NewSlot).Task;

    /// <summary>Connects and sends one identification frame, then waits for this id's announcement.</summary>
    public async Task<(AudioSocketTestPeer Peer, StateRecording Recording)> IdentifyAsync(Guid id, CancellationToken token)
    {
        var announced = AnnouncedAs(id.ToString());
        var peer = await AudioSocketTestPeer.ConnectAsync(Port, token);
        await peer.SendAsync(AudioSocketFrames.Uuid(id), token);
        return (peer, await announced.WaitAsync(token));
    }

    /// <summary>
    /// Connects with <paramref name="id"/> until the server admits and announces the connection: a peer
    /// refused at the accept reads its close at once and connects again. The token is the one bound.
    /// </summary>
    public async Task<(AudioSocketTestPeer Peer, StateRecording Recording)> IdentifyUntilAdmittedAsync(Guid id, CancellationToken token)
    {
        var announced = AnnouncedAs(id.ToString());
        while (true)
        {
            var peer = await AudioSocketTestPeer.ConnectAsync(Port, token);
            try
            {
                await peer.SendAsync(AudioSocketFrames.Uuid(id), token);
            }
            catch (IOException)
            {
                peer.Dispose();
                continue;
            }

            if (await Task.WhenAny(announced, peer.Closed).WaitAsync(token) == announced)
                return (peer, await announced);

            peer.Dispose();
        }
    }

    /// <summary>Set by a test that disposed the server itself, so the harness does not dispose it a second time.</summary>
    public bool ServerDisposedByTest { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _subscription.Dispose();
        if (!ServerDisposedByTest)
            await Server.DisposeAsync();
        _ownListener?.Dispose();
    }

    private static TaskCompletionSource<StateRecording> NewSlot(string _) =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The far end of one AudioSocket connection, with a pending read that completes when the server closes it.</summary>
internal sealed class AudioSocketTestPeer : IDisposable
{
    private readonly TcpClient _client;

    private AudioSocketTestPeer(TcpClient client)
    {
        _client = client;
        Closed = ReadUntilClosedAsync(client.GetStream());
    }

    /// <summary>Completes when the server closes the connection (end of stream or reset).</summary>
    public Task Closed { get; }

    public static async Task<AudioSocketTestPeer> ConnectAsync(int port, CancellationToken token)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            return new AudioSocketTestPeer(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SendAsync(byte[] frame, CancellationToken token = default)
    {
        var stream = _client.GetStream();
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    /// <summary>FIN: the far end closes its sending half without a hangup frame.</summary>
    public void ShutdownSend() => _client.Client.Shutdown(SocketShutdown.Send);

    public void Dispose() => _client.Dispose();

    private static async Task ReadUntilClosedAsync(NetworkStream stream)
    {
        var buffer = new byte[256];
        try
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
                // The ARI server writes nothing unless a consumer does; anything read is discarded.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // A reset, or this side disposed: either way the connection is gone.
        }
    }
}

/// <summary>AudioSocket frames: one byte of type, two of big-endian length, then the payload.</summary>
internal static class AudioSocketFrames
{
    public static byte[] Build(AudioFrameType type, byte[] payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>The UUID in RFC 4122 order, most significant byte first, as Asterisk sends it.</summary>
    public static byte[] Uuid(Guid id) => Build(AudioFrameType.Uuid, id.ToByteArray(bigEndian: true));

    public static byte[] Hangup() => Build(AudioFrameType.Hangup, []);

    public static byte[] Error() => Build(AudioFrameType.Error, [0x01]);

    public static byte[] Audio(byte fill) => Build(AudioFrameType.Audio, Enumerable.Repeat(fill, 320).ToArray());
}

/// <summary>
/// A real ARI WebSocket audio server on a loopback port, whose announcements are handed to a per-test
/// subscription action and recorded by channel id (the upgrade path's last segment).
/// </summary>
internal sealed class WebSocketEndingHarness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StateRecording>> _announced = new(StringComparer.Ordinal);
    private readonly IDisposable _subscription;
    private int _disposed;
    private int _announcements;

    /// <summary>How many streams the server has announced so far.</summary>
    public int Announcements => Volatile.Read(ref _announcements);

    private WebSocketEndingHarness(WebSocketAudioServer server, int port, AudioServerOptions options, CountingLogger<WebSocketAudioServer> logger, Action<StateRecording> subscribe)
    {
        Server = server;
        Port = port;
        Options = options;
        Logger = logger;
        _subscription = server.OnStreamConnected.Subscribe(stream =>
        {
            Interlocked.Increment(ref _announcements);
            var recording = new StateRecording(stream);
            try
            {
                subscribe(recording);
            }
            finally
            {
                _announced.GetOrAdd(stream.ChannelId, NewSlot).TrySetResult(recording);
            }
        });
    }

    public WebSocketAudioServer Server { get; }

    public int Port { get; }

    public AudioServerOptions Options { get; }

    public CountingLogger<WebSocketAudioServer> Logger { get; }

    public static async Task<WebSocketEndingHarness> StartAsync(Action<StateRecording> subscribe, int maxStreams = 1000)
    {
        var logger = new CountingLogger<WebSocketAudioServer>();
        AudioServerOptions? options = null;
        var (server, port) = await LoopbackServerBind.StartAsync(
            p =>
            {
                options = new AudioServerOptions
                {
                    WebSocketPort = p,
                    ListenAddress = "127.0.0.1",
                    MaxConcurrentStreams = maxStreams,
                    IdleTimeout = TimeSpan.FromSeconds(60),
                };
                return new WebSocketAudioServer(options, logger, TimeProvider.System);
            },
            s => s.StartAsync());
        return new WebSocketEndingHarness(server, port, options!, logger, subscribe);
    }

    public Task<StateRecording> AnnouncedAs(string id) => _announced.GetOrAdd(id, NewSlot).Task;

    public async Task<(WebSocketTestPeer Peer, StateRecording Recording)> ConnectAsync(string id, CancellationToken token)
    {
        var announced = AnnouncedAs(id);
        var peer = await WebSocketTestPeer.ConnectAsync(Port, id, token);
        return (peer, await announced.WaitAsync(token));
    }

    /// <summary>Connects to <paramref name="id"/>'s path until the server admits and announces it.</summary>
    public async Task<(WebSocketTestPeer Peer, StateRecording Recording)> ConnectUntilAdmittedAsync(string id, CancellationToken token)
    {
        var announced = AnnouncedAs(id);
        while (true)
        {
            WebSocketTestPeer peer;
            try
            {
                peer = await WebSocketTestPeer.ConnectAsync(Port, id, token);
            }
            catch (WebSocketException)
            {
                continue;
            }

            return (peer, await announced.WaitAsync(token));
        }
    }

    /// <summary>Set by a test that disposed the server itself, so the harness does not dispose it a second time.</summary>
    public bool ServerDisposedByTest { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _subscription.Dispose();
        if (!ServerDisposedByTest)
            await Server.DisposeAsync();
    }

    private static TaskCompletionSource<StateRecording> NewSlot(string _) =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The far end of one WebSocket audio connection.</summary>
internal sealed class WebSocketTestPeer : IDisposable
{
    private readonly ClientWebSocket _client;

    private WebSocketTestPeer(ClientWebSocket client) => _client = client;

    public static async Task<WebSocketTestPeer> ConnectAsync(int port, string id, CancellationToken token)
    {
        var client = new ClientWebSocket();
        try
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/{id}"), token);
            return new WebSocketTestPeer(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Sends the close frame and does not wait for the server's answer.</summary>
    public Task SendCloseAsync() =>
        _client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

    /// <summary>Aborts the connection: no close frame, the socket is torn down.</summary>
    public void Abort() => _client.Abort();

    public void Dispose() => _client.Dispose();
}

/// <summary>Disposes everything added to it, in order, when it is disposed.</summary>
internal sealed class DisposableSet : IDisposable
{
    private readonly List<IDisposable> _items = [];

    public int Count => _items.Count;

    public T Add<T>(T item) where T : IDisposable
    {
        _items.Add(item);
        return item;
    }

    public void Dispose()
    {
        foreach (var item in _items)
            item.Dispose();
        _items.Clear();
    }
}
