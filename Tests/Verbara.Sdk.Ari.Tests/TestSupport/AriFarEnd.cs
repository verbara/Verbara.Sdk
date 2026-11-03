using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// The Asterisk side of an ARI client on one loopback port: it answers an events upgrade and sends events when a test
/// says so, drops a connection when a test says so, and answers a REST request with what <see cref="Respond"/> says.
/// Every TCP connection counts in <see cref="Dials"/>, whatever it asked for, so a test that expects none sees any.
/// </summary>
internal sealed class AriFarEnd : IAsyncDisposable
{
    private readonly TcpListener _server = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<TaskCompletionSource> _upgraded = [];
    private readonly List<string> _requests = [];
    private readonly Task _run;
    private Upgraded? _current;
    private int _dials;
    private int _upgrades;

    public AriFarEnd()
    {
        _server.Start();
        Port = ((IPEndPoint)_server.LocalEndpoint).Port;
        _run = Task.Run(RunAsync);
    }

    public int Port { get; }

    /// <summary>The address an <c>AriClientOptions.BaseUrl</c> reaches this far end on.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>TCP connections accepted so far, of any kind.</summary>
    public int Dials => Volatile.Read(ref _dials);

    /// <summary>Events upgrades answered so far.</summary>
    public int Upgrades => Volatile.Read(ref _upgrades);

    /// <summary>The request line of every REST request so far, in arrival order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
                return [.. _requests];
        }
    }

    /// <summary>What a REST request is answered with, from its request line. Read for each request.</summary>
    public Func<string, (int Status, string Body)> Respond { get; set; } = _ => (200, "{}");

    /// <summary>Completes once <paramref name="count"/> upgrades have been answered. The caller bounds the wait.</summary>
    public Task WhenUpgraded(int count) => UpgradeSignal(count - 1).Task;

    /// <summary>Sends one event of type <paramref name="type"/> on the current events connection.</summary>
    public Task SendAsync(string type)
    {
        var json = Encoding.UTF8.GetBytes($$"""{"type":"{{type}}","application":"test-app"}""");
        return Current().Socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, _stop.Token);
    }

    /// <summary>Resets the current events connection, with no close frame.</summary>
    public void Drop()
    {
        var current = Current();
        current.Tcp.Client.LingerState = new LingerOption(true, 0);
        current.Tcp.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _server.Stop();
        await _run.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private Upgraded Current() =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException("No events connection has been upgraded.");

    private TaskCompletionSource UpgradeSignal(int index)
    {
        lock (_gate)
        {
            while (_upgraded.Count <= index)
                _upgraded.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return _upgraded[index];
        }
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        List<Task> serving = [];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connection = await _server.AcceptTcpClientAsync(ct);
                Interlocked.Increment(ref _dials);
                serving.Add(Task.Run(() => ServeAsync(connection, ct), CancellationToken.None));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (SocketException)
        {
            // The listener was stopped under the accept.
        }

        foreach (var task in serving)
            await task.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task ServeAsync(TcpClient connection, CancellationToken ct)
    {
        using var held = connection;
        var stream = connection.GetStream();
        var request = await ReadHeadAsync(stream, ct);
        var lines = request.Split("\r\n");
        var key = lines
            .Where(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["Sec-WebSocket-Key:".Length..].Trim())
            .FirstOrDefault();

        if (key is null)
        {
            await AnswerRestAsync(stream, lines[0], ct);
            return;
        }

        await WebSocketAudioServer.SendUpgradeResponseAsync(stream, key, ct);
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        Volatile.Write(ref _current, new Upgraded(connection, socket));
        var answered = Interlocked.Increment(ref _upgrades);
        UpgradeSignal(answered - 1).TrySetResult();
        await AnswerCloseAsync(socket, ct);
    }

    private async Task AnswerRestAsync(Stream stream, string requestLine, CancellationToken ct)
    {
        lock (_gate)
            _requests.Add(requestLine);

        var (status, body) = Respond(requestLine);
        var content = Encoding.UTF8.GetBytes(body);
        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} Status\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(content, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task AnswerCloseAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
            }
        }
        catch (WebSocketException)
        {
            // The connection was dropped or aborted, by this far end or by the client.
        }
        catch (IOException)
        {
            // The same, seen from the stream.
        }
        catch (ObjectDisposedException)
        {
            // Dropped under the receive.
        }
    }

    // One byte at a time: an upgraded connection's first frames must stay in the stream for the WebSocket.
    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (head.Length < 8192)
        {
            if (await stream.ReadAsync(one, ct) == 0)
                break;

            head.Append((char)one[0]);
            if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n")
                break;
        }

        return head.ToString();
    }

    private sealed record Upgraded(TcpClient Tcp, WebSocket Socket);
}
