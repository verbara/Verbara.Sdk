using System.Net;
using System.Net.Sockets;
using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Agi.Tests.Server;

public sealed class FastAgiServerTests : IAsyncDisposable
{
    private FastAgiServer? _sut;

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null)
            await _sut.DisposeAsync();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task StartAsync_ShouldSetIsRunningTrue()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        _sut.IsRunning.Should().BeFalse();
        await _sut.StartAsync();
        _sut.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldSetIsRunningFalse()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();
        _sut.IsRunning.Should().BeTrue();

        await _sut.StopAsync();
        _sut.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptTcpConnections()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);
        await _sut.StartAsync();

        // Attempt to connect a TCP client
        using var client = new TcpClient();
        var act = async () => await client.ConnectAsync(IPAddress.Loopback, port);
        await act.Should().NotThrowAsync();

        client.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldRejectNewConnections()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();
        await _sut.StopAsync();

        // After stop, new connections should fail
        using var client = new TcpClient();
        var act = async () => await client.ConnectAsync(IPAddress.Loopback, port);
        await act.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task ConnectionTimeout_ShouldDefaultToFiveMinutes()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        _sut.ConnectionTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ConnectionTimeout_ShouldBeConfigurable()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance)
        {
            ConnectionTimeout = TimeSpan.FromSeconds(30)
        };

        _sut.ConnectionTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HandleConnection_ShouldTimeout_WhenScriptHangs()
    {
        var port = GetAvailablePort();
        var hangingScript = new HangingScript();
        var strategy = Substitute.For<IMappingStrategy>();
        strategy.Resolve(Arg.Any<AgiRequest>()).Returns(hangingScript);

        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance)
        {
            ConnectionTimeout = TimeSpan.FromMilliseconds(200)
        };
        await _sut.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        // Send minimal AGI request headers
        var stream = client.GetStream();
        var headers = "agi_network: yes\nagi_network_script: hang\nagi_channel: SIP/test\nagi_uniqueid: 1.1\n\n";
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(headers));

        // Wait for connection timeout + margin
        await Task.Delay(500);

        // The hanging script should have been cancelled via timeout
        hangingScript.WasCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopIfRunning()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();
        _sut.IsRunning.Should().BeTrue();

        await _sut.DisposeAsync();
        _sut.IsRunning.Should().BeFalse();
        _sut = null; // Prevent double dispose in DisposeAsync
    }

    private sealed class HangingScript : IAgiScript
    {
        public bool WasCancelled { get; private set; }

        public async ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
