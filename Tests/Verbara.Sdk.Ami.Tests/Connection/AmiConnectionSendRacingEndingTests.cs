using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A send that has passed the connected check and is waiting for the write lock when the caller ends the connection
/// throws <see cref="AmiNotConnectedException"/> — what the same send throws a moment later — never the
/// <see cref="NullReferenceException"/> of a writer the ending released, nor the cancellation of the ending's cleanup.
/// </summary>
/// <remarks>
/// <para>
/// The race is made deterministic through a seam that lives only in this test: the write lock, a private field reached
/// by reflection (a test project is not AOT-published, and an <c>extern</c> accessor reads as unmanaged code to the
/// code scan), is held by the test. A send called on the test thread runs synchronously up to its wait on that lock,
/// so once the call returns it has passed the connected check and waits. The disconnect is then started with a token
/// already cancelled, so its Logoff gives up at once instead of queueing on the lock, and it is not awaited before the
/// lock is released: a cleanup that takes the write lock would wait for the release.
/// </para>
/// <para>
/// It runs over the in-memory <see cref="PipedSocket"/> and over the real <see cref="PipelineSocketConnection"/> with a
/// loopback peer. Every wait is bounded by <see cref="Bound"/> and ends on the signal it waits for.
/// </para>
/// </remarks>
public sealed class AmiConnectionSendRacingEndingTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    public const string Piped = "piped";
    public const string Pipeline = "pipeline";
    public const string Untyped = "SendActionAsync";
    public const string Typed = "SendActionAsync<TResponse>";
    public const string EventGenerating = "SendEventGeneratingActionAsync";

    private static SemaphoreSlim WriteLock(AmiConnection connection) =>
        (SemaphoreSlim)(typeof(AmiConnection).GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(AmiConnection), "_writeLock")).GetValue(connection)!;

    [Theory]
    [InlineData(Untyped, Piped)]
    [InlineData(Typed, Piped)]
    [InlineData(EventGenerating, Piped)]
    [InlineData(Untyped, Pipeline)]
    [InlineData(Typed, Pipeline)]
    [InlineData(EventGenerating, Pipeline)]
    public async Task SendActionAsync_ShouldThrowNotConnected_WhenTheCallerDisconnectsWhileTheSendWaitsForTheWriteLock(
        string send, string transport)
    {
        await using var harness = await Harness.ConnectAsync(transport);
        var connection = harness.Connection;
        var writeLock = WriteLock(connection);
        await writeLock.WaitAsync().WaitAsync(Bound);

        // Runs synchronously up to its wait on the write lock: past the connected check, waiting.
        var sending = StartSend(connection, send);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var disconnecting = connection.DisconnectAsync(cancelled.Token).AsTask();
        var stateWhileEnding = connection.State;
        await harness.SocketDisposed.WaitAsync(Bound);

        writeLock.Release();
        var thrown = await Record.ExceptionAsync(() => sending.WaitAsync(Bound));
        await disconnecting.WaitAsync(Bound);

        using (new AssertionScope())
        {
            stateWhileEnding.Should().Be(AmiConnectionState.Disconnecting, "the caller's ending was recorded before the lock was released");
            thrown.Should().BeOfType<AmiNotConnectedException>(
                "a send overtaken by the caller's ending reports the connection as not connected, as the same send a moment later does");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// A send whose write is blocked because the peer stopped reading holds the write lock for as long as the peer does
    /// not read. The caller's <see cref="AmiConnection.DisposeAsync"/> still completes: its Logoff is bounded, so the
    /// release, whose disposal of the socket is what ends the blocked write, is reached; the send then reports the
    /// connection as not connected.
    /// </summary>
    /// <remarks>
    /// Over the real transport only: the in-memory <see cref="PipedSocket"/> never completes the pipe the connection
    /// writes on, so a write blocked on it stays blocked after the connection disposes it. The payload is far larger than
    /// the pipe's pause threshold and the loopback socket's buffers, with the peer's receive buffer shrunk, so the send's
    /// flush is still waiting when the call returns to the test.
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_ShouldComplete_WhenASendIsBlockedByAPeerThatStoppedReading()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
        }), new PipelineSocketConnectionFactory(), NullLogger<AmiConnection>.Instance);
        TcpClient? server = null;
        Task disposing = Task.CompletedTask;
        try
        {
            var served = Task.Run(async () =>
            {
                server = await listener.AcceptTcpClientAsync(peerCts.Token);
                server.ReceiveBufferSize = 4096;
                await new LoopbackPeer(server.GetStream()).CompleteLoginAsync(peerCts.Token);
                // From here the peer reads nothing.
            }, peerCts.Token);
            await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            await served.WaitAsync(Bound);

            var sending = connection.SendActionAsync(new CommandAction { Command = new string('x', 16 * 1024 * 1024) }).AsTask();
            disposing = connection.DisposeAsync().AsTask();
            var disposeThrown = await Record.ExceptionAsync(() => disposing.WaitAsync(Bound));
            var sendThrown = await Record.ExceptionAsync(() => sending.WaitAsync(Bound));

            using (new AssertionScope())
            {
                disposeThrown.Should().BeNull(
                    "the ending's Logoff gives up on a write lock that a blocked send holds, and the release that unblocks it runs");
                sendThrown.Should().BeOfType<AmiNotConnectedException>(
                    "the send's write was ended by the caller's ending, which reports the connection as not connected");
                connection.State.Should().Be(AmiConnectionState.Disconnected);
            }
        }
        finally
        {
            // The peer goes away, which ends a write the unfixed code leaves blocked, and with it that code's ending.
            server?.Dispose();
            _ = await Record.ExceptionAsync(() => disposing.WaitAsync(Bound));
            await connection.DisposeAsync().AsTask().WaitAsync(Bound);
            listener.Stop();
        }
    }

    private static Task StartSend(AmiConnection connection, string send)
    {
        switch (send)
        {
            case Untyped:
                return connection.SendActionAsync(new PingAction()).AsTask();
            case Typed:
                return connection.SendActionAsync<ManagerResponse>(new PingAction()).AsTask();
            case EventGenerating:
                var enumerator = connection.SendEventGeneratingActionAsync(new StatusAction()).GetAsyncEnumerator();
                var first = enumerator.MoveNextAsync().AsTask();
                return DrainAsync(enumerator, first);
            default:
                throw new ArgumentOutOfRangeException(nameof(send), send, "Not a send this test makes.");
        }
    }

    private static async Task DrainAsync(IAsyncEnumerator<ManagerEvent> enumerator, Task<bool> first)
    {
        await using (enumerator)
        {
            if (!await first)
                return;

            while (await enumerator.MoveNextAsync())
            {
                // The events, if any, are not what this test asserts.
            }
        }
    }

    /// <summary>A connected <see cref="AmiConnection"/> over one transport, and the disposal of its socket.</summary>
    internal sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener? _listener;
        private readonly CancellationTokenSource _peerCts = new(Bound * 3);
        private Task _peer = Task.CompletedTask;

        private Harness(AmiConnection connection, Task socketDisposed, TcpListener? listener)
        {
            Connection = connection;
            SocketDisposed = socketDisposed;
            _listener = listener;
        }

        public AmiConnection Connection { get; }

        /// <summary>Completes once the connection has disposed the socket of its session.</summary>
        public Task SocketDisposed { get; private set; }

        public static async Task<Harness> ConnectAsync(string transport)
        {
            if (transport == Piped)
            {
                var sockets = new PipedSocketFactory();
                var connection = Create(sockets, "localhost", 5038);
                var harness = new Harness(connection, Task.CompletedTask, listener: null);
                harness._peer = Task.Run(async () =>
                {
                    var socket = await sockets.NextAsync(harness._peerCts.Token);
                    await socket.CompleteLoginAsync(harness._peerCts.Token);
                    await socket.AnswerPingsAsync(harness._peerCts.Token);
                });
                await connection.ConnectAsync().AsTask().WaitAsync(Bound);
                harness.SocketDisposed = sockets.Created[0].Disposed;
                return harness;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var factory = new SignalingSocketFactory(new PipelineSocketConnectionFactory());
            var tcp = Create(factory, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            var loopback = new Harness(tcp, Task.CompletedTask, listener);
            loopback._peer = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(loopback._peerCts.Token);
                var peer = new LoopbackPeer(client.GetStream());
                await peer.CompleteLoginAsync(loopback._peerCts.Token);
                await peer.ReadUntilClosedAsync(loopback._peerCts.Token);
            });
            await tcp.ConnectAsync().AsTask().WaitAsync(Bound);
            loopback.SocketDisposed = factory.Created[0].Disposed;
            return loopback;
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
            await _peerCts.CancelAsync();
            await _peer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _listener?.Stop();
            _listener?.Dispose();
            _peerCts.Dispose();
        }

        private static AmiConnection Create(ISocketConnectionFactory sockets, string host, int port) =>
            new(Options.Create(new AmiConnectionOptions
            {
                Hostname = host,
                Port = port,
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = false,
            }), sockets, NullLogger<AmiConnection>.Instance);
    }

    /// <summary>Wraps a real transport so a test can wait on the connection's disposal of it.</summary>
    internal sealed class SignalingSocketFactory(ISocketConnectionFactory inner) : ISocketConnectionFactory
    {
        private readonly Lock _gate = new();
        private readonly List<SignalingSocket> _created = [];

        public IReadOnlyList<SignalingSocket> Created
        {
            get
            {
                lock (_gate)
                {
                    return [.. _created];
                }
            }
        }

        public ISocketConnection Create()
        {
            var socket = new SignalingSocket(inner.Create());
            lock (_gate)
            {
                _created.Add(socket);
            }

            return socket;
        }

        public ISocketConnection FromStream(Stream stream) => throw new NotSupportedException("The AMI client only dials out.");
    }

    internal sealed class SignalingSocket(ISocketConnection inner) : ISocketConnection
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Disposed => _disposed.Task;

        public bool IsConnected => inner.IsConnected;

        public PipeReader Input => inner.Input;

        public PipeWriter Output => inner.Output;

        public ValueTask ConnectAsync(string hostname, int port, bool useSsl = false, CancellationToken cancellationToken = default) =>
            inner.ConnectAsync(hostname, port, useSsl, cancellationToken);

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => inner.CloseAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            _disposed.TrySetResult();
        }
    }

    /// <summary>The Asterisk side of a loopback TCP connection: the banner, the MD5 challenge, the login, the version probe.</summary>
    internal sealed class LoopbackPeer(NetworkStream stream)
    {
        private readonly StringBuilder _pending = new();

        public async Task CompleteLoginAsync(CancellationToken ct)
        {
            await WriteAsync("Asterisk Call Manager/6.0.0\r\n", ct);
            var challenge = await ReadActionAsync(ct) ?? throw new InvalidOperationException("No challenge.");
            await WriteAsync($"Response: Success\r\nActionID: {PipedSocket.ActionIdOf(challenge)}\r\nChallenge: abc123\r\n\r\n", ct);
            var login = await ReadActionAsync(ct) ?? throw new InvalidOperationException("No login.");
            await WriteAsync($"Response: Success\r\nActionID: {PipedSocket.ActionIdOf(login)}\r\nMessage: Authentication accepted\r\n\r\n", ct);
            var core = await ReadActionAsync(ct) ?? throw new InvalidOperationException("No version probe.");
            await WriteAsync($"Response: Success\r\nActionID: {PipedSocket.ActionIdOf(core)}\r\nAsteriskVersion: 20.0.0\r\n\r\n", ct);
        }

        /// <summary>Reads and drops every action until the connection closes the socket.</summary>
        public async Task ReadUntilClosedAsync(CancellationToken ct)
        {
            while (await ReadActionAsync(ct) is not null)
            {
                // A send this test races is never answered: the ending decides how it ends.
            }
        }

        private async Task WriteAsync(string text, CancellationToken ct)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await stream.FlushAsync(ct);
        }

        private async Task<string?> ReadActionAsync(CancellationToken ct)
        {
            var buffer = new byte[4096];
            while (true)
            {
                var text = _pending.ToString();
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    _pending.Remove(0, end + 4);
                    return text[..(end + 4)];
                }

                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, ct);
                }
                catch (IOException)
                {
                    return null;
                }

                if (read == 0)
                    return null;

                _pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
    }
}
