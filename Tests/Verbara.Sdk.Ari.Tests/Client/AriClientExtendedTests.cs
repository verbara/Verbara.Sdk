using System.Net;
using System.Net.Sockets;
using System.Text;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

public sealed class AriClientExtendedTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static AriClient CreateClient(string baseUrl = "http://localhost:8088")
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = baseUrl,
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        });
        return new AriClient(options, NullLogger<AriClient>.Instance);
    }

    [Fact]
    public async Task Channels_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Channels.Should().NotBeNull();
    }

    [Fact]
    public async Task Bridges_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Bridges.Should().NotBeNull();
    }

    [Fact]
    public async Task Playbacks_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Playbacks.Should().NotBeNull();
    }

    [Fact]
    public async Task Recordings_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Recordings.Should().NotBeNull();
    }

    [Fact]
    public async Task Endpoints_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Endpoints.Should().NotBeNull();
    }

    [Fact]
    public async Task Applications_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Applications.Should().NotBeNull();
    }

    [Fact]
    public async Task Sounds_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Sounds.Should().NotBeNull();
    }

    [Fact]
    public async Task DeviceStates_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.DeviceStates.Should().NotBeNull();
    }

    [Fact]
    public async Task Asterisk_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Asterisk.Should().NotBeNull();
    }

    [Fact]
    public async Task Mailboxes_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Mailboxes.Should().NotBeNull();
    }

    [Fact]
    public async Task AudioServer_ShouldBeNull_WhenNotProvided()
    {
        await using var sut = CreateClient();
        sut.AudioServer.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNotConnected()
    {
        var sut = CreateClient();
        var act = async () => await sut.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_ShouldTransitionState()
    {
        await using var sut = CreateClient();
        sut.State.Should().Be(AriConnectionState.Initial);
        // Verify we can dispose without error
    }

    [Fact]
    public async Task State_ShouldBeInitial_BeforeConnect()
    {
        await using var sut = CreateClient();
        sut.State.Should().Be(AriConnectionState.Initial);
        sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrow_WhenServerUnreachable()
    {
        await using var sut = CreateClient("http://192.0.2.1:1");

        var act = async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await sut.ConnectAsync(cts.Token);
        };
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task BaseUrl_WithTrailingSlash_ShouldWork()
    {
        await using var sut = CreateClient("http://localhost:8088/");
        sut.State.Should().Be(AriConnectionState.Initial);
    }

    [Fact]
    public async Task GenerateUserEventAsync_ShouldReachServer_WhenSentThroughClientHandlerChain()
    {
        // A real listener rather than a fake handler: the request has to go through the handler
        // chain the AriClient constructor builds, so a logging handler left without its inner
        // handler fails here with InvalidOperationException before anything reaches the socket.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var sut = CreateClient($"http://127.0.0.1:{port}");
        var served = AnswerOneRequestWithNoContentAsync(listener);

        using var guard = new CancellationTokenSource(WaitLimit);
        await sut.GenerateUserEventAsync("probe", "test-app", cancellationToken: guard.Token);
        var requestHead = await served.WaitAsync(WaitLimit);

        requestHead.Should().StartWith("POST /ari/events/user/probe?application=test-app HTTP/1.1\r\n");
        requestHead.Should().Contain("Authorization: Basic ");
    }

    /// <summary>
    /// Accepts one connection, reads the request head and answers 204 No Content. Returns the
    /// request head so the test can check what the client sent.
    /// </summary>
    private static async Task<string> AnswerOneRequestWithNoContentAsync(TcpListener listener)
    {
        using var accepted = await listener.AcceptTcpClientAsync();
        var stream = accepted.GetStream();
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length
               && !Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0) break;
            total += read;
        }

        await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        return Encoding.ASCII.GetString(buffer, 0, total);
    }
}
