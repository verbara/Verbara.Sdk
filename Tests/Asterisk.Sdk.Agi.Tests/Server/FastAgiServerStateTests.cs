using System.Net;
using System.Net.Sockets;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Agi.Tests.Server;

public sealed class FastAgiServerStateTests : IAsyncDisposable
{
    private FastAgiServer? _sut;

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null)
            await _sut.DisposeAsync();
        GC.SuppressFinalize(this);
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
    public void NewServer_ShouldHaveStoppedState()
    {
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(GetAvailablePort(), strategy, NullLogger<FastAgiServer>.Instance);

        _sut.State.Should().Be(AgiServerState.Stopped);
    }

    [Fact]
    public async Task StartAsync_ShouldTransitionToListening()
    {
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(GetAvailablePort(), strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();

        _sut.State.Should().Be(AgiServerState.Listening);

        await _sut.StopAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldTransitionToStopped()
    {
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(GetAvailablePort(), strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();
        await _sut.StopAsync();

        _sut.State.Should().Be(AgiServerState.Stopped);
    }

    [Fact]
    public async Task StartAsync_ShouldThrow_WhenFaulted()
    {
        // Use a port that is already in use to force a Faulted state
        using var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        var blockedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(blockedPort, strategy, NullLogger<FastAgiServer>.Instance);

        // First start attempt should fault because port is in use
        var firstStart = async () => await _sut.StartAsync();
        await firstStart.Should().ThrowAsync<SocketException>();

        _sut.State.Should().Be(AgiServerState.Faulted);

        // Second start attempt should throw InvalidOperationException
        var secondStart = async () => await _sut.StartAsync();
        await secondStart.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Faulted*");
    }
}
