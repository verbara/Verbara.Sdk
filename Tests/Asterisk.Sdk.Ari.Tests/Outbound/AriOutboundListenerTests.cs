using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ari.Tests.Outbound;

public sealed class AriOutboundListenerTests
{
    private static (AriOutboundListener listener, AriOutboundListenerOptions options) CreateListener(
        Action<AriOutboundListenerOptions>? configure = null)
    {
        var options = new AriOutboundListenerOptions
        {
            ListenAddress = "127.0.0.1",
            Port = 0, // ephemeral
            Path = "/ari/events",
            ConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };
        configure?.Invoke(options);
        var listener = new AriOutboundListener(
            Options.Create(options),
            NullLogger<AriOutboundListener>.Instance);
        return (listener, options);
    }

    private static async Task<ClientWebSocket> ConnectClientAsync(
        int port,
        string path = "/ari/events",
        string? app = "myapp",
        string? basicAuthHeader = null)
    {
        var client = new ClientWebSocket();
        if (basicAuthHeader is not null)
            client.Options.SetRequestHeader("Authorization", basicAuthHeader);

        var appQuery = app is not null ? $"?app={Uri.EscapeDataString(app)}" : string.Empty;
        var uri = new Uri($"ws://127.0.0.1:{port}{path}{appQuery}");
        await client.ConnectAsync(uri, CancellationToken.None);
        return client;
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptHandshake_WhenAllHeadersValid()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            using var client = await ConnectClientAsync(listener.BoundPort);
            client.State.Should().Be(WebSocketState.Open);

            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldRejectHandshake_WhenPathMismatch()
    {
        var (listener, _) = CreateListener(o => o.Path = "/ari/events");
        await listener.StartAsync();
        try
        {
            // Wrong path — client upgrade should fail.
            var client = new ClientWebSocket();
            var uri = new Uri($"ws://127.0.0.1:{listener.BoundPort}/wrong/path?app=myapp");
            var act = async () => await client.ConnectAsync(uri, CancellationToken.None);

            await act.Should().ThrowAsync<WebSocketException>();
            listener.ActiveConnectionCount.Should().Be(0);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldRejectHandshake_WhenAuthMismatch()
    {
        var (listener, _) = CreateListener(o =>
        {
            o.ExpectedUsername = "alice";
            o.ExpectedPassword = "secret";
        });
        await listener.StartAsync();
        try
        {
            var badAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrong"));
            var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Authorization", badAuth);
            var uri = new Uri($"ws://127.0.0.1:{listener.BoundPort}/ari/events?app=myapp");

            var act = async () => await client.ConnectAsync(uri, CancellationToken.None);
            await act.Should().ThrowAsync<WebSocketException>();

            listener.ActiveConnectionCount.Should().Be(0);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldRejectHandshake_WhenAppNotAllowed()
    {
        var (listener, _) = CreateListener(o => o.AllowedApplications.Add("only-this-one"));
        await listener.StartAsync();
        try
        {
            var client = new ClientWebSocket();
            var uri = new Uri($"ws://127.0.0.1:{listener.BoundPort}/ari/events?app=other");

            var act = async () => await client.ConnectAsync(uri, CancellationToken.None);
            await act.Should().ThrowAsync<WebSocketException>();

            listener.ActiveConnectionCount.Should().Be(0);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptHandshake_WhenAllowedListEmpty()
    {
        var (listener, _) = CreateListener(); // empty AllowedApplications
        await listener.StartAsync();
        try
        {
            using var client = await ConnectClientAsync(listener.BoundPort, app: "anything");
            client.State.Should().Be(WebSocketState.Open);
            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task EventDispatch_ShouldEmitTypedEvent_WhenAsteriskSendsJson()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            AriOutboundConnection? accepted = null;
            using var sub = listener.OnConnectionAccepted.Subscribe(c => accepted = c);

            using var client = await ConnectClientAsync(listener.BoundPort);
            (await WaitForAsync(() => accepted is not null, TimeSpan.FromSeconds(2))).Should().BeTrue();

            AriEvent? received = null;
            using var evtSub = accepted!.Events.Subscribe(e => received = e);

            const string json = """{"type":"StasisStart","application":"myapp","timestamp":"2026-04-19T10:00:00Z","args":["arg1"],"channel":{"id":"ch-1","name":"PJSIP/test","state":"Up"}}""";
            await client.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            (await WaitForAsync(() => received is not null, TimeSpan.FromSeconds(2))).Should().BeTrue();
            received.Should().BeOfType<Asterisk.Sdk.Ari.Events.StasisStartEvent>();
            received!.Type.Should().Be("StasisStart");
            received.Application.Should().Be("myapp");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleConnections_ShouldCoexist_ForSameApplication()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            using var a = await ConnectClientAsync(listener.BoundPort, app: "app1");
            using var b = await ConnectClientAsync(listener.BoundPort, app: "app1");
            using var c = await ConnectClientAsync(listener.BoundPort, app: "app1");

            (await WaitForAsync(() => listener.ActiveConnectionCount == 3, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
            listener.GetByApplication("app1").Should().HaveCount(3);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleApps_ShouldBeIndexedSeparately()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            using var a = await ConnectClientAsync(listener.BoundPort, app: "app1");
            using var b = await ConnectClientAsync(listener.BoundPort, app: "app2");
            using var c = await ConnectClientAsync(listener.BoundPort, app: "app2");

            (await WaitForAsync(() => listener.ActiveConnectionCount == 3, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
            listener.GetByApplication("app1").Should().HaveCount(1);
            listener.GetByApplication("app2").Should().HaveCount(2);
            listener.GetByApplication("nonexistent").Should().BeEmpty();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task IdleTimeout_ShouldCloseConnection_WhenNoFramesSent()
    {
        var (listener, _) = CreateListener(o => o.ConnectionIdleTimeout = TimeSpan.FromMilliseconds(250));
        await listener.StartAsync();
        try
        {
            using var client = await ConnectClientAsync(listener.BoundPort);
            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();

            (await WaitForAsync(() => listener.ActiveConnectionCount == 0, TimeSpan.FromSeconds(3)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task ActiveConnectionCount_ShouldReflectReality()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            listener.ActiveConnectionCount.Should().Be(0);

            using (var a = await ConnectClientAsync(listener.BoundPort))
            {
                (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                    .Should().BeTrue();

                using (var b = await ConnectClientAsync(listener.BoundPort))
                {
                    (await WaitForAsync(() => listener.ActiveConnectionCount == 2, TimeSpan.FromSeconds(2)))
                        .Should().BeTrue();

                    await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                        .Should().BeTrue();
                }

                await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                (await WaitForAsync(() => listener.ActiveConnectionCount == 0, TimeSpan.FromSeconds(2)))
                    .Should().BeTrue();
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldRemoveConnectionFromActiveSet()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            AriOutboundConnection? accepted = null;
            using var sub = listener.OnConnectionAccepted.Subscribe(c => accepted = c);

            using var client = await ConnectClientAsync(listener.BoundPort);
            (await WaitForAsync(() => accepted is not null, TimeSpan.FromSeconds(2))).Should().BeTrue();

            await accepted!.DisconnectAsync();

            (await WaitForAsync(() => listener.ActiveConnectionCount == 0, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
            accepted.IsConnected.Should().BeFalse();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldCloseAllActiveSessions()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();

        using var a = await ConnectClientAsync(listener.BoundPort, app: "app1");
        using var b = await ConnectClientAsync(listener.BoundPort, app: "app2");
        (await WaitForAsync(() => listener.ActiveConnectionCount == 2, TimeSpan.FromSeconds(2)))
            .Should().BeTrue();

        await listener.DisposeAsync();

        listener.IsRunning.Should().BeFalse();
        listener.ActiveConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldBeIdempotent()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            // Second Start should be a no-op and not throw.
            await listener.StartAsync();
            listener.IsRunning.Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptAuth_WhenBasicAuthHeaderMatchesExpected()
    {
        var (listener, _) = CreateListener(o =>
        {
            o.ExpectedUsername = "alice";
            o.ExpectedPassword = "secret";
        });
        await listener.StartAsync();
        try
        {
            var auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));
            using var client = await ConnectClientAsync(listener.BoundPort, basicAuthHeader: auth);
            client.State.Should().Be(WebSocketState.Open);
            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }
}

public sealed class AriOutboundListenerOptionsValidatorTests
{
    [Fact]
    public void Validate_ShouldSucceed_ForDefaults()
    {
        var validator = new AriOutboundListenerOptionsValidator();
        var result = validator.Validate(null, new AriOutboundListenerOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_ShouldFail_WhenPortOutOfRange(int port)
    {
        var validator = new AriOutboundListenerOptionsValidator();
        var opts = new AriOutboundListenerOptions { Port = port };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPathDoesNotStartWithSlash()
    {
        var validator = new AriOutboundListenerOptionsValidator();
        var opts = new AriOutboundListenerOptions { Path = "ari/events" };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }
}
