using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

public sealed class AriClientStateTests
{
    private static AriClient CreateClient()
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        });
        return new AriClient(options, NullLogger<AriClient>.Instance);
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(10); // fence-allow: LOOP-DRIVER — AriClient exposes State but no state-change signal; bounded by the caller's timeout
        }
        return predicate();
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopReconnecting_WhenDisposedWhileReconnecting()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(500)
        }), NullLogger<AriClient>.Instance);

        // Accept the events socket, complete the upgrade, then drop it: the client falls into its backoff.
        var firstConnection = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, CancellationToken.None);
        });
        await sut.ConnectAsync();
        await firstConnection;

        (await WaitForAsync(() => sut.State == AriConnectionState.Reconnecting, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("dropping the events socket starts the reconnect backoff");

        await sut.DisposeAsync();

        // Several backoff periods later, a disposed client must not have dialled back in.
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var redial = async () =>
        {
            using var late = await server.AcceptTcpClientAsync(window.Token);
        };
        await redial.Should().ThrowAsync<OperationCanceledException>("a disposed client must not reconnect");
    }

    [Fact]
    public async Task State_ShouldBeInitial_WhenNewClientCreated()
    {
        await using var sut = CreateClient();

        sut.State.Should().Be(AriConnectionState.Initial);
    }

    [Fact]
    public async Task IsConnected_ShouldBeFalse_WhenNotConnected()
    {
        await using var sut = CreateClient();

        sut.IsConnected.Should().BeFalse();
    }
}
