using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>
/// The fake's own fragmenting knobs, read by a raw <see cref="ClientWebSocket"/> so that no recognizer
/// stands between the fake and the assertion: a test of the recognizer's reassembly is only worth what
/// these establish about the frames it is given.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class DeepgramFakeServerTests : IAsyncLifetime
{
    /// <summary>Upper bound on any single read. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] Terminator = """{"type":"CloseStream"}"""u8.ToArray();

    private readonly DeepgramFakeServer _server = new();

    [Fact]
    public async Task Session_ShouldDeliverOneTextMessageAsMoreThanOneRead_WhenTextFrameFragmentBytesIsSet()
    {
        // Arrange
        var message = DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true);
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(message);
        _server.TextFrameFragmentBytes = 16;
        _server.Start();
        using var client = await ConnectAsync();

        // Act
        var reads = await ReadOneMessageAsync(client);

        // Assert
        reads.Count.Should().BeGreaterThanOrEqualTo(2, "a message sent in 16-byte fragments is not one read");
        reads[0].EndOfMessage.Should().BeFalse("the first read is a fragment, not the whole message");
        reads.Should().OnlyContain(r => r.Type == WebSocketMessageType.Text);
        Assemble(reads).Should().Be(message, "the fragments together are the message, byte for byte");
    }

    /// <summary>Control: with the knob at its default the fake sends each message in one frame, as before it existed.</summary>
    [Fact]
    public async Task Session_ShouldDeliverOneTextMessageAsOneRead_WhenTextFrameFragmentBytesIsZero()
    {
        // Arrange
        var message = DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true);
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(message);
        _server.Start();
        using var client = await ConnectAsync();

        // Act
        var reads = await ReadOneMessageAsync(client);

        // Assert
        reads.Should().ContainSingle().Which.EndOfMessage.Should().BeTrue();
        Assemble(reads).Should().Be(message);
    }

    [Fact]
    public async Task Session_ShouldSendTheRestOfTheMessage_WhenTheWithheldFragmentIsReleased()
    {
        // Arrange
        var message = DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true);
        var withheld = new WithheldFinalFragment(message);
        using var client = await ConnectWithWithheldMessageAsync(withheld);

        // Act
        var first = await ReadAsync(client);
        withheld.Release();
        var rest = await ReadOneMessageAsync(client);

        // Assert
        first.Type.Should().Be(WebSocketMessageType.Text);
        first.EndOfMessage.Should().BeFalse("only the first fragment was sent");
        first.Text.Should().Be(message[..withheld.FirstFragmentBytes]);
        Assemble([first, .. rest]).Should().Be(message);
    }

    [Fact]
    public async Task Session_ShouldCloseInTheMiddleOfTheMessage_WhenTheWithheldFragmentIsClosed()
    {
        // Arrange
        var withheld = new WithheldFinalFragment(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));
        using var client = await ConnectWithWithheldMessageAsync(withheld);

        // Act
        var first = await ReadAsync(client);
        withheld.Close();
        var next = await ReadAsync(client);

        // Assert
        first.EndOfMessage.Should().BeFalse("only the first fragment was sent");
        next.Type.Should().Be(WebSocketMessageType.Close, "the close frame follows the unfinished message");
        client.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task Session_ShouldEndTheTransportInTheMiddleOfTheMessage_WhenTheWithheldFragmentIsAborted()
    {
        // Arrange
        var withheld = new WithheldFinalFragment(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));
        using var client = await ConnectWithWithheldMessageAsync(withheld);

        // Act
        var first = await ReadAsync(client);
        withheld.Abort();
        var next = async () => await ReadAsync(client);

        // Assert
        first.EndOfMessage.Should().BeFalse("only the first fragment was sent");
        await next.Should().ThrowAsync<WebSocketException>("the transport ended with no close frame");
    }

    /// <summary>
    /// Starts a session that sends nothing on connect and answers the terminator with
    /// <paramref name="withheld"/>'s first fragment, and sends the terminator.
    /// </summary>
    private async Task<ClientWebSocket> ConnectWithWithheldMessageAsync(WithheldFinalFragment withheld)
    {
        _server.ResultMessages.Clear();
        _server.WithholdFinalFragment = withheld;
        _server.Start();
        var client = await ConnectAsync();
        await client.SendAsync(Terminator, WebSocketMessageType.Text, true, CancellationToken.None);
        withheld.SendFirstFragment();
        await withheld.FirstFragmentSent.WaitAsync(ReadTimeout);
        return client;
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/v1/listen"), CancellationToken.None)
            .WaitAsync(ReadTimeout);
        return client;
    }

    private static async Task<List<Read>> ReadOneMessageAsync(ClientWebSocket client)
    {
        var reads = new List<Read>();
        Read read;
        do
        {
            read = await ReadAsync(client);
            reads.Add(read);
        }
        while (!read.EndOfMessage);

        return reads;
    }

    private static async Task<Read> ReadAsync(ClientWebSocket client)
    {
        var buffer = new byte[65536];
        var result = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask().WaitAsync(ReadTimeout);
        return new Read(result.MessageType, result.EndOfMessage, buffer.AsSpan(0, result.Count).ToArray());
    }

    private static string Assemble(IEnumerable<Read> reads)
    {
        var assembled = new ArrayBufferWriter<byte>();
        foreach (var read in reads)
            assembled.Write(read.Bytes);
        return Encoding.UTF8.GetString(assembled.WrittenSpan);
    }

    private sealed record Read(WebSocketMessageType Type, bool EndOfMessage, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _server.DisposeAsync();
}
