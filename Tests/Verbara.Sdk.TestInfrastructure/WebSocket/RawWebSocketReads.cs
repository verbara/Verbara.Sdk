using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>One <c>ReceiveAsync</c> as a raw <see cref="ClientWebSocket"/> saw it.</summary>
/// <param name="Type">The frame's message type.</param>
/// <param name="EndOfMessage">Whether the transport marked this read as the message's last.</param>
/// <param name="Bytes">The bytes this read carried.</param>
public sealed record RawWebSocketRead(WebSocketMessageType Type, bool EndOfMessage, byte[] Bytes)
{
    /// <summary>The read's bytes as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// Reads a fake server's frames through a raw <see cref="ClientWebSocket"/>, so that no client under
/// test stands between the fake and the assertion. A fake's self-test is only worth what it
/// establishes about the frames the client under test will be given.
/// </summary>
public static class RawWebSocketReads
{
    /// <summary>Upper bound on any single connect or read. Reaching it is a failure, never a pace.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Connects a raw client to <c>ws://127.0.0.1:<paramref name="port"/>/</c>.</summary>
    public static async Task<ClientWebSocket> ConnectAsync(int port)
    {
        var client = new ClientWebSocket();
        try
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None)
                .WaitAsync(ReadTimeout).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Sends <paramref name="text"/> as one whole text message.</summary>
    public static async Task SendTextAsync(ClientWebSocket client, string text)
    {
        ArgumentNullException.ThrowIfNull(client);
        await client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None)
            .WaitAsync(ReadTimeout).ConfigureAwait(false);
    }

    /// <summary>One read, into a buffer as large as the clients under test use.</summary>
    public static async Task<RawWebSocketRead> ReadAsync(ClientWebSocket client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var buffer = new byte[65536];
        var result = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask()
            .WaitAsync(ReadTimeout).ConfigureAwait(false);
        return new RawWebSocketRead(result.MessageType, result.EndOfMessage, buffer.AsSpan(0, result.Count).ToArray());
    }

    /// <summary>Every read of the next message, up to and including the one marked end of message.</summary>
    public static async Task<List<RawWebSocketRead>> ReadOneMessageAsync(ClientWebSocket client)
    {
        var reads = new List<RawWebSocketRead>();
        RawWebSocketRead read;
        do
        {
            read = await ReadAsync(client).ConfigureAwait(false);
            reads.Add(read);
        }
        while (!read.EndOfMessage && read.Type != WebSocketMessageType.Close);

        return reads;
    }

    /// <summary>Every read until the server's close frame, which is not included.</summary>
    public static async Task<List<RawWebSocketRead>> ReadUntilCloseAsync(ClientWebSocket client)
    {
        var reads = new List<RawWebSocketRead>();
        while (true)
        {
            var read = await ReadAsync(client).ConfigureAwait(false);
            if (read.Type == WebSocketMessageType.Close)
                return reads;
            reads.Add(read);
        }
    }

    /// <summary>The reads' bytes concatenated, as UTF-8 text.</summary>
    public static string Assemble(IEnumerable<RawWebSocketRead> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var assembled = new ArrayBufferWriter<byte>();
        foreach (var read in reads)
            assembled.Write(read.Bytes);
        return Encoding.UTF8.GetString(assembled.WrittenSpan);
    }
}
