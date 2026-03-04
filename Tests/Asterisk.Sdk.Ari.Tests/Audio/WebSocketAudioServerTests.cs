using System.Text;
using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public class WebSocketAudioServerTests
{
    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldParseWsKeyAndChannelId()
    {
        const string request = "GET /ws/ch-12345 HTTP/1.1\r\n" +
                               "Host: localhost:9093\r\n" +
                               "Upgrade: websocket\r\n" +
                               "Connection: Upgrade\r\n" +
                               "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                               "Sec-WebSocket-Version: 13\r\n\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().Be("dGhlIHNhbXBsZSBub25jZQ==");
        channelId.Should().Be("ch-12345");
    }

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldHandleSimplePath()
    {
        const string request = "GET /my-channel-id HTTP/1.1\r\n" +
                               "Sec-WebSocket-Key: abc123==\r\n\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().Be("abc123==");
        channelId.Should().Be("my-channel-id");
    }

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldReturnNull_ForEmptyStream()
    {
        using var stream = new MemoryStream([]);
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().BeNull();
        channelId.Should().BeNull();
    }

    [Fact]
    public async Task SendUpgradeResponseAsync_ShouldWriteHttp101()
    {
        using var stream = new MemoryStream();

        await WebSocketAudioServer.SendUpgradeResponseAsync(stream, "dGhlIHNhbXBsZSBub25jZQ==", CancellationToken.None);

        stream.Position = 0;
        var response = Encoding.ASCII.GetString(stream.ToArray());
        response.Should().StartWith("HTTP/1.1 101 Switching Protocols");
        response.Should().Contain("Upgrade: websocket");
        response.Should().Contain("Connection: Upgrade");
        response.Should().Contain("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    [Fact]
    public void AudioServerOptions_ShouldHaveCorrectDefaults()
    {
        var options = new AudioServerOptions();

        options.AudioSocketPort.Should().Be(9092);
        options.WebSocketPort.Should().Be(9093);
        options.ListenAddress.Should().Be("0.0.0.0");
        options.MaxConcurrentStreams.Should().Be(1000);
        options.DefaultFormat.Should().Be("slin16");
        options.IdleTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }
}
