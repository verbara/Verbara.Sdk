using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>How the server's side of a <see cref="RawWebSocketPeer"/>'s connection ended, as the peer read it.</summary>
internal enum RawConnectionEnd
{
    /// <summary>End of stream: the server closed the connection.</summary>
    Fin,

    /// <summary>The read failed with <see cref="SocketError.ConnectionReset"/>.</summary>
    Reset,

    /// <summary>This peer closed or reset its own socket first.</summary>
    Local,
}

/// <summary>One frame a <see cref="RawWebSocketPeer"/> read from the server.</summary>
/// <param name="Opcode">The frame's opcode: 1 text, 2 binary, 8 close.</param>
/// <param name="CloseStatus">For a close frame with a body, its status code; otherwise <see langword="null"/>.</param>
/// <param name="PayloadLength">The payload's length in bytes.</param>
/// <param name="Timestamp">When the peer finished reading it, in <see cref="Stopwatch"/> ticks.</param>
internal sealed record RawWebSocketFrame(int Opcode, int? CloseStatus, int PayloadLength, long Timestamp);

/// <summary>
/// The client end of one WebSocket connection, framed by hand on a plain socket, so a test controls every byte the
/// server receives and records every frame the server sends. Unlike <c>ClientWebSocket</c> it answers nothing by
/// itself: a close frame from the server is recorded and answered only when the test says so, and the connection can
/// end with a close frame, with FIN and no close frame, or with a reset.
/// </summary>
/// <remarks>
/// Once upgraded, a read loop records each frame the server sends, in order, and how the connection ended. Its
/// order is the wire's: a close frame recorded before <see cref="Ended"/> reached the peer before the server
/// released the connection.
/// </remarks>
internal sealed class RawWebSocketPeer : IDisposable
{
    public const int OpcodeText = 1;
    public const int OpcodeBinary = 2;
    public const int OpcodeClose = 8;

    private static readonly byte[] MaskKey = [0x37, 0xfa, 0x21, 0x3d];

    private readonly Socket _socket;
    private readonly ConcurrentQueue<RawWebSocketFrame> _frames = new();
    private readonly TaskCompletionSource<RawWebSocketFrame> _firstClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _closeReceived = new();
    private readonly TaskCompletionSource<RawConnectionEnd> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readsAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _endedAt;
    private int _closedLocally;

    private RawWebSocketPeer(Socket socket, bool readPaused)
    {
        _socket = socket;
        if (!readPaused)
            _readsAllowed.TrySetResult();
    }

    /// <summary>Every frame the server sent so far, in the order read.</summary>
    public IReadOnlyList<RawWebSocketFrame> Frames => [.. _frames];

    /// <summary>The close frames the server sent so far.</summary>
    public IReadOnlyList<RawWebSocketFrame> Closes => [.. _frames.Where(f => f.Opcode == OpcodeClose)];

    /// <summary>Completes with the first close frame the server sends.</summary>
    public Task<RawWebSocketFrame> FirstClose => _firstClose.Task;

    /// <summary>Completes when the connection ends, with how it ended.</summary>
    public Task<RawConnectionEnd> Ended => _ended.Task;

    /// <summary>When <see cref="Ended"/> completed, in <see cref="Stopwatch"/> ticks; 0 before.</summary>
    public long EndedAt => Interlocked.Read(ref _endedAt);

    /// <summary>
    /// Blocks until the server's first close frame is read, up to <paramref name="timeout"/>. For an observer that
    /// must hold the thread it was called on.
    /// </summary>
    public bool WaitForClose(TimeSpan timeout) => _closeReceived.Wait(timeout);

    /// <summary>Connects to the loopback port. Nothing is sent until <see cref="UpgradeAsync"/>.</summary>
    /// <param name="port">The server's port.</param>
    /// <param name="token">Bounds the connect.</param>
    /// <param name="readPaused">
    /// Whether the read loop waits for <see cref="ResumeReading"/> after the upgrade, so the server's writes fill the
    /// connection. The receive buffer is then kept small.
    /// </param>
    public static async Task<RawWebSocketPeer> ConnectAsync(int port, CancellationToken token, bool readPaused = false)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (readPaused)
                socket.ReceiveBufferSize = 4096;
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), token);
            return new RawWebSocketPeer(socket, readPaused);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends the upgrade for <c>/ws/{id}</c>, reads the 101 up to its blank line and nothing after it, then starts
    /// the read loop.
    /// </summary>
    public async Task UpgradeAsync(string id, CancellationToken token)
    {
        var request =
            $"GET /ws/{id} HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";
        await _socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None, token);

        var response = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(response))
        {
            var n = await _socket.ReceiveAsync(one, SocketFlags.None, token);
            if (n == 0)
                throw new InvalidOperationException("The server closed the connection during the upgrade.");
            response.Add(one[0]);
        }

        var status = Encoding.ASCII.GetString([.. response]);
        if (!status.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            throw new InvalidOperationException($"The upgrade was not accepted: {status}");

        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>Lets a read loop started paused begin reading.</summary>
    public void ResumeReading() => _readsAllowed.TrySetResult();

    /// <summary>Sends one binary message in one frame.</summary>
    public Task SendBinaryAsync(byte[] payload) => SendFrameAsync(OpcodeBinary, payload);

    /// <summary>Sends one text message in one frame.</summary>
    public Task SendTextAsync(string text) => SendFrameAsync(OpcodeText, Encoding.UTF8.GetBytes(text));

    /// <summary>Sends a close frame with <paramref name="status"/> and no reason. The connection stays open.</summary>
    public Task SendCloseAsync(ushort status = 1000)
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(body, status);
        return SendFrameAsync(OpcodeClose, body);
    }

    /// <summary>Answers the server's first close frame with a close 1000 as soon as it is read; a failed send is ignored.</summary>
    public void AnswerCloseWhenReceived() => _ = AnswerWhenReceivedAsync();

    /// <summary>Resets the connection as soon as the server's first close frame is read, without answering it.</summary>
    public void ResetWhenCloseReceived() => _ = ResetWhenReceivedAsync();

    /// <summary>Ends the connection with a reset: a linger-zero close puts an RST on the wire, and no close frame.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _closedLocally, 1);
        try
        {
            _socket.LingerState = new LingerOption(enable: true, seconds: 0);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already closed: there is nothing left to reset.
        }

        _socket.Dispose();
    }

    /// <summary>Closes this side's sending half with no close frame: FIN, the read loop keeps reading.</summary>
    public void ShutdownSend() => _socket.Shutdown(SocketShutdown.Send);

    public void Dispose()
    {
        Interlocked.Exchange(ref _closedLocally, 1);
        _socket.Dispose();
    }

    private async Task AnswerWhenReceivedAsync()
    {
        await FirstClose;
        try
        {
            await SendCloseAsync();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The server released the connection first: the answer has nowhere to go.
        }
    }

    private async Task ResetWhenReceivedAsync()
    {
        await FirstClose;
        Reset();
    }

    private async Task SendFrameAsync(int opcode, byte[] payload)
    {
        var length = payload.Length;
        var header = length < 126 ? 2 : length <= ushort.MaxValue ? 4 : 10;
        var frame = new byte[header + 4 + length];
        frame[0] = (byte)(0x80 | opcode);
        if (length < 126)
        {
            frame[1] = (byte)(0x80 | length);
        }
        else if (length <= ushort.MaxValue)
        {
            frame[1] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)length);
        }
        else
        {
            frame[1] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)length);
        }

        MaskKey.CopyTo(frame, header);
        for (var i = 0; i < length; i++)
            frame[header + 4 + i] = (byte)(payload[i] ^ MaskKey[i % 4]);

        await _writeLock.WaitAsync();
        try
        {
            await _socket.SendAsync(frame, SocketFlags.None);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        await _readsAllowed.Task;
        try
        {
            var head = new byte[2];
            while (true)
            {
                if (!await ReadExactlyAsync(head))
                {
                    End(RawConnectionEnd.Fin);
                    return;
                }

                var opcode = head[0] & 0x0F;
                var masked = (head[1] & 0x80) != 0;
                long length = head[1] & 0x7F;
                if (length == 126)
                {
                    var ext = new byte[2];
                    if (!await ReadExactlyAsync(ext)) { End(RawConnectionEnd.Fin); return; }
                    length = BinaryPrimitives.ReadUInt16BigEndian(ext);
                }
                else if (length == 127)
                {
                    var ext = new byte[8];
                    if (!await ReadExactlyAsync(ext)) { End(RawConnectionEnd.Fin); return; }
                    length = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                }

                var mask = new byte[4];
                if (masked && !await ReadExactlyAsync(mask)) { End(RawConnectionEnd.Fin); return; }

                var payload = new byte[length];
                if (!await ReadExactlyAsync(payload)) { End(RawConnectionEnd.Fin); return; }
                if (masked)
                {
                    for (var i = 0; i < payload.Length; i++)
                        payload[i] ^= mask[i % 4];
                }

                int? status = opcode == OpcodeClose && payload.Length >= 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(payload)
                    : null;
                var frame = new RawWebSocketFrame(opcode, status, payload.Length, Stopwatch.GetTimestamp());
                _frames.Enqueue(frame);
                if (opcode == OpcodeClose && _firstClose.TrySetResult(frame))
                    _closeReceived.Set();
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset && Volatile.Read(ref _closedLocally) == 0)
        {
            End(RawConnectionEnd.Reset);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            End(RawConnectionEnd.Local);
        }
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None);
            if (n == 0)
                return false;
            read += n;
        }

        return true;
    }

    private void End(RawConnectionEnd how)
    {
        Interlocked.CompareExchange(ref _endedAt, Stopwatch.GetTimestamp(), 0);
        _ended.TrySetResult(how);
    }

    private static bool EndsWithBlankLine(List<byte> bytes) =>
        bytes.Count >= 4
        && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n';
}
