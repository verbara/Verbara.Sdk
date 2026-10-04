using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// A connection an ARI audio server closes at the accept because <c>MaxConcurrentStreams</c> places are
/// already held is written to the log at Warning and counted on <c>audio.connections.refused</c>, by the
/// server that refused it; a refusal decided after the server's stop began is the stop, and is neither.
/// </summary>
/// <remarks>
/// <para>
/// Every test here reads instruments of the <c>Verbara.Sdk.Ari.Audio</c> meter, which are process-wide
/// and carry no tags, so the class runs in <see cref="AudioStreamMetricsGroup"/>, alone.
/// </para>
/// <para>
/// No wait is a delay. A refused client is read to its end, which proves the server closed it; the
/// report is then awaited as the N-th <c>StreamLimitReached</c> entry, within a bound. The server records
/// the count before it writes the Warning, and a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// callback runs inside <c>Counter.Add</c>, so once the N-th Warning is seen the N-th count is already
/// in. A pin that expects no report ends its wait on a step the server takes after the refusal branch:
/// the stop's completion, the admitted stream's announcement, or the loop asking for its next accept.
/// </para>
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class AudioServerRefusalReportTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Far above any wait here, so an admitted connection that never identifies stays open.</summary>
    private static readonly TimeSpan LongIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The refusal counter, read by its name on the meter: the contract an exporter sees.</summary>
    private const string RefusedInstrument = "audio.connections.refused";

    private const string RefusalEvent = "StreamLimitReached";

    private const int Limit = 2;

    /// <summary>The ARI audio server under test.</summary>
    public enum Transport
    {
        /// <summary><see cref="AudioSocketServer"/>.</summary>
        AudioSocket,

        /// <summary><see cref="WebSocketAudioServer"/>.</summary>
        WebSocket,
    }

    // ------------------------------------------------------------ refusals while running

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task AcceptLoop_ShouldLogAWarningAndCountEachRefusal_WhenTenConnectionsArriveAtALimitOfTwo(Transport transport)
    {
        // Arrange — a running server whose limit is 2; the refusal counter is read by name
        using var refused = new TransportFailureCounter(TransportFailureCounter.MeterName, RefusedInstrument);
        var (server, port) = await LoopbackServerBind.StartAsync(
            p => ServerUnderTest.Create(transport, NewOptions(p, p)),
            s => s.StartAsync());
        await using var _ = server;

        // Act — 10 connections, one after another, none of which ends. The accept queue is FIFO, so the
        // first two are the admitted ones; their reads are started before the refused ones are read.
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 10; i++)
                clients.Add(await ConnectAsync(port));
            var admittedReads = clients.Take(Limit).Select(c => c.GetStream().ReadAsync(new byte[1]).AsTask()).ToList();

            var closedByServer = 0;
            foreach (var client in clients.Skip(Limit))
                closedByServer += await ReadsToEndAsync(client) ? 1 : 0;
            var reported = await WhenReportedAsync(server.Logger, 8);

            // Assert
            using (new AssertionScope())
            {
                closedByServer.Should().Be(8, "the 8 connections over the limit are closed by the server");
                reported.Should().BeTrue(
                    $"every refused connection is reported, but after the bound {Refusals(server.Logger)} " +
                    $"StreamLimitReached entries and a counter delta of {refused.Total} were observed");
                AssertRefusalWarnings(server.Logger, 8);
                refused.Total.Should().Be(8, "each refusal is counted once on audio.connections.refused");
                admittedReads.Should().OnlyContain(read => !read.IsCompleted, "the 2 admitted connections stay open");
                server.Options.Admission.Held.Should().Be(Limit);
            }
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task AcceptLoop_ShouldReportTheRefusalAtTheServerThatRefused_WhenAnotherServerHoldsTheSharedPlaces()
    {
        // Arrange — one options instance for both servers, as AddVerbara registers them; two identified
        // AudioSocket calls hold both places
        using var refused = new TransportFailureCounter(TransportFailureCounter.MeterName, RefusedInstrument);
        var (pair, audioSocketPort, webSocketPort) = await LoopbackServerBind.StartAsync(
            (a, w) => new ServersSharingOneOptions(NewOptions(a, w)),
            p => p.StartAsync());
        await using var _ = pair;

        var announced = 0;
        var bothAnnounced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var announcements = pair.AudioSocket.Server.OnStreamConnected.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref announced) == Limit)
                bothAnnounced.TrySetResult();
        });
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < Limit; i++)
            {
                var call = await ConnectAsync(audioSocketPort);
                clients.Add(call);
                await call.GetStream().WriteAsync(UuidFrame(Guid.NewGuid()));
            }
            await bothAnnounced.Task.WaitAsync(SignalTimeout);

            // Act — 3 connections arrive at the WebSocket server
            var refusedClients = new List<TcpClient>();
            for (var i = 0; i < 3; i++)
                refusedClients.Add(await ConnectAsync(webSocketPort));
            clients.AddRange(refusedClients);

            var closedByServer = 0;
            foreach (var client in refusedClients)
                closedByServer += await ReadsToEndAsync(client) ? 1 : 0;
            var reported = await WhenReportedAsync(pair.WebSocket.Logger, 3);

            // Assert
            using (new AssertionScope())
            {
                closedByServer.Should().Be(3);
                reported.Should().BeTrue(
                    $"the WebSocket server refused them, but after the bound {Refusals(pair.WebSocket.Logger)} " +
                    $"StreamLimitReached entries from it and a counter delta of {refused.Total} were observed");
                AssertRefusalWarnings(pair.WebSocket.Logger, 3);
                Refusals(pair.AudioSocket.Logger).Should().Be(0, "the AudioSocket server holds the places but refused nothing");
                refused.Total.Should().Be(3);
                pair.Options.Admission.Held.Should().Be(Limit);
            }
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }
    }

    // ------------------------------------------------------------ pins: what is not a refusal report

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task AcceptLoop_ShouldCloseWithoutReporting_WhenTheRefusalIsDecidedAfterTheStopBegan(Transport transport)
    {
        // Arrange — every place held; the accept seam hands over one connected loopback client, and only
        // once the test releases it
        using var refused = new TransportFailureCounter(TransportFailureCounter.MeterName, RefusedInstrument);
        var server = ServerUnderTest.Create(transport, NewOptions(0, 0));
        await using var _ = server;

        using var side = new TcpListener(IPAddress.Loopback, 0);
        side.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)side.LocalEndpoint).Port);
        var handedOver = await side.AcceptTcpClientAsync();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        bool? runningAtHandOff = null;
        server.AcceptOverride = async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                runningAtHandOff = server.IsRunning;
                return handedOver;
            }

            await Task.Delay(Timeout.Infinite, ct); // fence-allow: SIMULATED-WORK — a pending accept with nothing to hand over, which ends only when the stop cancels it
            throw new InvalidOperationException("unreachable: the wait above ends only by cancellation");
        };
        await server.StartAsync();
        server.Options.Admission.TryEnter(Limit).Should().BeTrue();
        server.Options.Admission.TryEnter(Limit).Should().BeTrue();

        // Act — the stop clears the running flag in its first, synchronous statement, so once the call
        // has returned the stop has begun; only then does the accept hand the connection over
        var stop = server.StopAsync();
        server.IsRunning.Should().BeFalse("the stop has begun");
        release.SetResult();
        await stop.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            (await ReadsToEndAsync(peer)).Should().BeTrue("the connection is still closed, unread");
            calls.Should().Be(1);
            runningAtHandOff.Should().BeFalse("the refusal was decided after the stop began");
            server.Options.Admission.Held.Should().Be(Limit);
            Refusals(server.Logger).Should().Be(0, "a refusal decided once the stop began is the stop, not a full server");
            refused.Total.Should().Be(0);
            refused.Measurements.Should().Be(0);
        }
    }

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task AcceptLoop_ShouldNotReport_WhenTheConnectionIsAdmitted(Transport transport)
    {
        // Arrange
        using var refused = new TransportFailureCounter(TransportFailureCounter.MeterName, RefusedInstrument);
        var (server, port) = await LoopbackServerBind.StartAsync(
            p => ServerUnderTest.Create(transport, NewOptions(p, p)),
            s => s.StartAsync());
        await using var _ = server;
        var announced = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var announcement = server.Server.OnStreamConnected.Subscribe(stream => announced.TrySetResult(stream));

        // Act — one connection, served: the announcement comes after the admission branch
        using var tcp = transport == Transport.AudioSocket ? await ConnectAsync(port) : null;
        using var ws = transport == Transport.WebSocket ? new ClientWebSocket() : null;
        if (tcp is not null)
            await tcp.GetStream().WriteAsync(UuidFrame(Guid.NewGuid()));
        else
            await ws!.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/ch-admitted"), CancellationToken.None).WaitAsync(SignalTimeout);
        await announced.Task.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            server.ActiveStreamCount.Should().Be(1, "the connection is served as before");
            server.Options.Admission.Held.Should().Be(1);
            Refusals(server.Logger).Should().Be(0);
            refused.Total.Should().Be(0);
            refused.Measurements.Should().Be(0);
        }
    }

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task AcceptLoop_ShouldMoveNoStreamCount_WhenAConnectionIsRefused(Transport transport)
    {
        // Arrange — every place held; the accept seam is a listener this test owns, so it learns the
        // moment the loop asks for its next connection, which is after the refusal branch has ended
        using var opened = new TransportFailureCounter(TransportFailureCounter.MeterName, "audio.streams.opened");
        using var closed = new TransportFailureCounter(TransportFailureCounter.MeterName, "audio.streams.closed");
        var server = ServerUnderTest.Create(transport, NewOptions(0, 0));
        await using var _ = server;

        using var seam = new TcpListener(IPAddress.Loopback, 0);
        seam.Start();
        var calls = 0;
        var secondAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = ct =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                secondAccept.TrySetResult();
            return seam.AcceptTcpClientAsync(ct);
        };
        server.Options.Admission.TryEnter(Limit).Should().BeTrue();
        server.Options.Admission.TryEnter(Limit).Should().BeTrue();
        await server.StartAsync();
        var activeBefore = server.ActiveStreamCount;

        // Act
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)seam.LocalEndpoint).Port);
        var closedByServer = await ReadsToEndAsync(client);
        await secondAccept.Task.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            closedByServer.Should().BeTrue("the connection was refused");
            opened.Total.Should().Be(0, "a refused connection never became a stream");
            closed.Total.Should().Be(0, "a refused connection never became a stream");
            server.ActiveStreamCount.Should().Be(activeBefore);
            server.Options.Admission.Held.Should().Be(Limit);
        }
    }

    // ------------------------------------------------------------ helpers

    private static AudioServerOptions NewOptions(int audioSocketPort, int webSocketPort) => new()
    {
        ListenAddress = "127.0.0.1",
        AudioSocketPort = audioSocketPort,
        WebSocketPort = webSocketPort,
        MaxConcurrentStreams = Limit,
        IdleTimeout = LongIdleTimeout,
    };

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SignalTimeout);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Whether the server closed this client: a read reaches the end of the stream, or a reset.</summary>
    private static async Task<bool> ReadsToEndAsync(TcpClient client)
    {
        var buffer = new byte[64];
        try
        {
            var stream = client.GetStream();
            while (await stream.ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout) > 0)
            {
            }
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Waits, within the bound, for the <paramref name="count"/>-th <c>StreamLimitReached</c> entry. The
    /// logger evaluates the predicate under its own lock, over the entries already written and then over
    /// each new one, so the increment is serialised and each entry is counted once.
    /// </summary>
    private static async Task<bool> WhenReportedAsync(ICapturedLog logger, int count)
    {
        var seen = 0;
        var nth = logger.WhenLogged(entry => entry.EventName == RefusalEvent && ++seen == count);
        try
        {
            await nth.WaitAsync(SignalTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static int Refusals(ICapturedLog logger) => logger.Entries.Count(entry => entry.EventName == RefusalEvent);

    private static void AssertRefusalWarnings(ICapturedLog logger, int count)
    {
        var reports = logger.Entries.Where(entry => entry.EventName == RefusalEvent).ToList();
        reports.Should().HaveCount(count, "one Warning per refused connection");
        reports.Should().OnlyContain(entry => entry.Level == LogLevel.Warning);
        foreach (var report in reports)
            report.Value("Limit").Should().Be(Limit, "the Warning names the limit that was reached");
    }

    private static byte[] UuidFrame(Guid uuid)
    {
        var frame = new byte[19];
        frame[0] = (byte)AudioFrameType.Uuid;
        frame[2] = 16;
        uuid.TryWriteBytes(frame.AsSpan(3), bigEndian: true, out _);
        return frame;
    }

    /// <summary>What a test reads from a server's capturing logger, whichever server it is.</summary>
    private interface ICapturedLog
    {
        IReadOnlyList<CapturedLogEntry> Entries { get; }

        Task<CapturedLogEntry> WhenLogged(Func<CapturedLogEntry, bool> match);
    }

    private sealed class CapturedLog<T> : ICapturedLog
    {
        public CapturingLogger<T> Logger { get; } = new();

        public IReadOnlyList<CapturedLogEntry> Entries => Logger.Entries;

        public Task<CapturedLogEntry> WhenLogged(Func<CapturedLogEntry, bool> match) => Logger.WhenLogged(match);
    }

    /// <summary>One ARI audio server, either transport, with its options and its capturing logger.</summary>
    private sealed class ServerUnderTest : IAsyncDisposable
    {
        private readonly AudioSocketServer? _audioSocket;
        private readonly WebSocketAudioServer? _webSocket;

        private ServerUnderTest(AudioServerOptions options, AudioSocketServer? audioSocket, WebSocketAudioServer? webSocket, ICapturedLog logger)
        {
            Options = options;
            _audioSocket = audioSocket;
            _webSocket = webSocket;
            Logger = logger;
        }

        public static ServerUnderTest Create(Transport transport, AudioServerOptions options)
        {
            if (transport == Transport.AudioSocket)
            {
                var log = new CapturedLog<AudioSocketServer>();
                return new ServerUnderTest(options, new AudioSocketServer(options, log.Logger), null, log);
            }

            var wsLog = new CapturedLog<WebSocketAudioServer>();
            return new ServerUnderTest(options, null, new WebSocketAudioServer(options, wsLog.Logger), wsLog);
        }

        public AudioServerOptions Options { get; }

        public ICapturedLog Logger { get; }

        public IAudioServer Server => (IAudioServer?)_audioSocket ?? _webSocket!;

        public bool IsRunning => _audioSocket?.IsRunning ?? _webSocket!.IsRunning;

        public int ActiveStreamCount => _audioSocket?.ActiveStreamCount ?? _webSocket!.ActiveStreamCount;

        public Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride
        {
            set
            {
                if (_audioSocket is not null)
                    _audioSocket.AcceptOverride = value;
                else
                    _webSocket!.AcceptOverride = value;
            }
        }

        public ValueTask StartAsync() => _audioSocket is not null ? _audioSocket.StartAsync() : _webSocket!.StartAsync();

        public Task StopAsync() => _audioSocket is not null ? _audioSocket.StopAsync().AsTask() : _webSocket!.StopAsync().AsTask();

        public ValueTask DisposeAsync() => _audioSocket is not null ? _audioSocket.DisposeAsync() : _webSocket!.DisposeAsync();
    }

    /// <summary>
    /// An AudioSocket server and a WebSocket server built on one options instance, started and disposed
    /// as one, so <see cref="LoopbackServerBind"/> can retry the pair on fresh servers.
    /// </summary>
    private sealed class ServersSharingOneOptions(AudioServerOptions options) : IAsyncDisposable
    {
        public AudioServerOptions Options { get; } = options;

        public ServerUnderTest AudioSocket { get; } = ServerUnderTest.Create(Transport.AudioSocket, options);

        public ServerUnderTest WebSocket { get; } = ServerUnderTest.Create(Transport.WebSocket, options);

        public async ValueTask StartAsync()
        {
            await AudioSocket.StartAsync();
            await WebSocket.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await WebSocket.DisposeAsync();
            }
            finally
            {
                await AudioSocket.DisposeAsync();
            }
        }
    }
}
