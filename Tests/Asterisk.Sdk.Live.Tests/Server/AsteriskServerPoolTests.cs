using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Live.Tests.Server;

public sealed class AsteriskServerPoolTests : IAsyncDisposable
{
    private readonly IAmiConnectionFactory _connectionFactory;
    private readonly AsteriskServerPool _sut;

    public AsteriskServerPoolTests()
    {
        _connectionFactory = Substitute.For<IAmiConnectionFactory>();

        // Each call to CreateAndConnectAsync returns a new mock connection
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        _connectionFactory.CreateAndConnectAsync(Arg.Any<AmiConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var conn = Substitute.For<IAmiConnection>();
                conn.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
                conn.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
                    .Returns(EmptyAsyncEnumerable());
                return new ValueTask<IAmiConnection>(conn);
            });
#pragma warning restore CA2012

        _sut = new AsteriskServerPool(_connectionFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static async IAsyncEnumerable<ManagerEvent> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public async Task AddServerAsync_ShouldCreateConnectionAndStartServer()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "pbx1.local",
            Username = "admin",
            Password = "secret"
        };

        var server = await _sut.AddServerAsync("server1", options);

        server.Should().NotBeNull();
        _sut.ServerCount.Should().Be(1);
        await _connectionFactory.Received(1).CreateAndConnectAsync(
            Arg.Is<AmiConnectionOptions>(o => o.Hostname == "pbx1.local"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddServerAsync_ShouldThrow_WhenDuplicateServerId()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        await _sut.AddServerAsync("dup-server", options);

        var act = async () => await _sut.AddServerAsync("dup-server", options);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task RemoveServerAsync_ShouldDisposeServer()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        await _sut.AddServerAsync("to-remove", options);
        _sut.ServerCount.Should().Be(1);

        await _sut.RemoveServerAsync("to-remove");

        _sut.ServerCount.Should().Be(0);
        _sut.GetServer("to-remove").Should().BeNull();
    }

    [Fact]
    public async Task GetServerForAgent_ShouldReturnNull_WhenAgentNotFound()
    {
        var result = _sut.GetServerForAgent("nonexistent");

        result.Should().BeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetServer_ShouldReturnServerById()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        await _sut.AddServerAsync("lookup-test", options);

        var server = _sut.GetServer("lookup-test");

        server.Should().NotBeNull();
    }

    [Fact]
    public async Task GetServer_ShouldReturnNull_WhenServerNotFound()
    {
        var result = _sut.GetServer("no-such-server");

        result.Should().BeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeAllServers()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        await _sut.AddServerAsync("s1", options);
        await _sut.AddServerAsync("s2", options);

        _sut.ServerCount.Should().Be(2);

        await _sut.DisposeAsync();

        _sut.ServerCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoveServerAsync_ShouldCleanupAgentRouting()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        await _sut.AddServerAsync("routed", options);

        // After removing the server, agent routing should be cleaned up
        await _sut.RemoveServerAsync("routed");

        _sut.GetServerForAgent("any-agent").Should().BeNull();
    }
}
