using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class VerbaraServerPoolTests : IAsyncDisposable
{
    private readonly IAmiConnectionFactory _connectionFactory;
    private readonly VerbaraServerPool _sut;

    /// <summary>Every connection the factory has returned to the pool, in order.</summary>
    private readonly List<IAmiConnection> _created = [];

    public VerbaraServerPoolTests()
    {
        _connectionFactory = Substitute.For<IAmiConnectionFactory>();

        // Each call to CreateAndConnectAsync returns a new mock connection, recorded in _created
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        _connectionFactory.CreateAndConnectAsync(Arg.Any<AmiConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var conn = NewConnection();
                _created.Add(conn);
                return new ValueTask<IAmiConnection>(conn);
            });
#pragma warning restore CA2012

        _sut = new VerbaraServerPool(_connectionFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static IAmiConnection NewConnection()
    {
        var conn = Substitute.For<IAmiConnection>();
        conn.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        conn.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyAsyncEnumerable());
        return conn;
    }

    /// <summary>A server over a connection its caller created and connected, as a cluster orchestrator hands one in.</summary>
    private static VerbaraServer HandedInServer(IAmiConnection connection) =>
        new(connection, NullLoggerFactory.Instance.CreateLogger<VerbaraServer>());

    private static AmiConnectionOptions Credentials() => new() { Username = "admin", Password = "secret" };

    private static async IAsyncEnumerable<ManagerEvent> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    // ── The pool releases the AMI connection behind every server it drops ────────────────────────────

    [Fact]
    public async Task RemoveServerAsync_ShouldDisposeTheConnection_WhenThePoolCreatedIt()
    {
        await _sut.AddServerAsync("created", Credentials());
        var connection = _created.Should().ContainSingle("the pool created one connection").Subject;

        await _sut.RemoveServerAsync("created");

        // "Remove and disconnect a server from the pool": the connection is released exactly once.
        await connection.Received(1).DisposeAsync();
    }

    /// <summary>
    /// The cluster failover shape: the caller creates and connects the connection itself, wraps it in a
    /// server and hands the server in. The pool accepted it, so the pool releases it on removal.
    /// </summary>
    [Fact]
    public async Task RemoveServerAsync_ShouldDisposeTheConnection_WhenTheServerWasHandedIn()
    {
        var connection = NewConnection();
        _sut.AddExistingServer("handed-in", HandedInServer(connection));

        await _sut.RemoveServerAsync("handed-in");

        await connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task AddServerAsync_ShouldDisposeTheNewConnection_WhenTheIdIsADuplicate()
    {
        await _sut.AddServerAsync("dup-server", Credentials());

        var act = async () => await _sut.AddServerAsync("dup-server", Credentials());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        _created.Should().HaveCount(2, "the pool created and connected the second connection before it found the id in use");
        // The rejected server's connection was created for it and never reaches the caller: the pool releases it.
        await _created[1].Received(1).DisposeAsync();
        // The server the pool already holds under that id keeps its connection.
        await _created[0].DidNotReceive().DisposeAsync();
    }

    /// <summary>One server the pool created and one handed in: the pool's disposal releases both connections.</summary>
    [Fact]
    public async Task DisposeAsync_ShouldDisposeEveryConnection_WhenThePoolHoldsTwoServers()
    {
        await _sut.AddServerAsync("created", Credentials());
        var created = _created.Should().ContainSingle("the pool created one connection").Subject;
        var handedIn = NewConnection();
        _sut.AddExistingServer("handed-in", HandedInServer(handedIn));

        await _sut.DisposeAsync();

        await created.Received(1).DisposeAsync();
        await handedIn.Received(1).DisposeAsync();
    }

    /// <summary>
    /// A pin, green before and after the pool releases connections: <c>AddExistingServer</c> never accepted
    /// the rejected server, so that server and its connection stay its caller's, and the held one keeps its own.
    /// </summary>
    [Fact]
    public async Task AddExistingServer_ShouldLeaveTheConnectionUndisposed_WhenTheIdIsADuplicate()
    {
        var held = NewConnection();
        var rejected = NewConnection();
        _sut.AddExistingServer("dup", HandedInServer(held));

        var act = () => _sut.AddExistingServer("dup", HandedInServer(rejected));

        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
        await rejected.DidNotReceive().DisposeAsync();
        await held.DidNotReceive().DisposeAsync();
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
            Arg.Is<AmiConnectionOptions>(o => o!.Hostname == "pbx1.local"),
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

    [Fact]
    public async Task MultiServer_ShouldTrackMultipleServers()
    {
        var s1 = await _sut.AddServerAsync("pbx-east", new AmiConnectionOptions
        {
            Hostname = "pbx-east.local", Username = "admin", Password = "secret"
        });
        var s2 = await _sut.AddServerAsync("pbx-west", new AmiConnectionOptions
        {
            Hostname = "pbx-west.local", Username = "admin", Password = "secret"
        });

        _sut.ServerCount.Should().Be(2);
        _sut.GetServer("pbx-east").Should().BeSameAs(s1);
        _sut.GetServer("pbx-west").Should().BeSameAs(s2);
    }

    [Fact]
    public async Task Servers_ShouldEnumerateAllServers()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        await _sut.AddServerAsync("s1", options);
        await _sut.AddServerAsync("s2", options);
        await _sut.AddServerAsync("s3", options);

        var servers = _sut.Servers.ToList();

        servers.Should().HaveCount(3);
        servers.Select(kvp => kvp.Key).Should().Contain(["s1", "s2", "s3"]);
    }

    [Fact]
    public async Task RemoveServerAsync_ShouldBeIdempotent_WhenServerNotFound()
    {
        // Removing a non-existent server should not throw
        await _sut.RemoveServerAsync("nonexistent");

        _sut.ServerCount.Should().Be(0);
    }

    [Fact]
    public async Task AgentRouting_ShouldTrackAgentLogin()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        var server = await _sut.AddServerAsync("pbx1", options);

        // Simulate agent login through the AgentManager
        server.Agents.OnAgentLogin("Agent/1001");

        _sut.GetServerForAgent("Agent/1001").Should().BeSameAs(server);
    }

    [Fact]
    public async Task AgentRouting_ShouldRemoveOnLogoff()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        var server = await _sut.AddServerAsync("pbx1", options);

        server.Agents.OnAgentLogin("Agent/2001");
        _sut.GetServerForAgent("Agent/2001").Should().NotBeNull();

        server.Agents.OnAgentLogoff("Agent/2001");
        _sut.GetServerForAgent("Agent/2001").Should().BeNull();
    }

    [Fact]
    public async Task AgentRouting_ShouldRouteToCorrectServer()
    {
        var options = new AmiConnectionOptions { Username = "admin", Password = "secret" };
        var east = await _sut.AddServerAsync("east", options);
        var west = await _sut.AddServerAsync("west", options);

        east.Agents.OnAgentLogin("Agent/1001");
        west.Agents.OnAgentLogin("Agent/2001");

        _sut.GetServerForAgent("Agent/1001").Should().BeSameAs(east);
        _sut.GetServerForAgent("Agent/2001").Should().BeSameAs(west);
    }

    [Fact]
    public void AddExistingServer_ShouldAddServerAndBeRetrievable()
    {
        var conn = Substitute.For<IAmiConnection>();
        conn.AsteriskVersion.Returns("20.0.0");
        conn.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        var server = new VerbaraServer(conn, NullLoggerFactory.Instance.CreateLogger<VerbaraServer>());

        _sut.AddExistingServer("failover-1", server);

        _sut.ServerCount.Should().Be(1);
        _sut.GetServer("failover-1").Should().BeSameAs(server);
    }

    [Fact]
    public void AddExistingServer_ShouldThrow_WhenDuplicateServerId()
    {
        var conn = Substitute.For<IAmiConnection>();
        conn.AsteriskVersion.Returns("20.0.0");
        conn.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        var server1 = new VerbaraServer(conn, NullLoggerFactory.Instance.CreateLogger<VerbaraServer>());
        var server2 = new VerbaraServer(conn, NullLoggerFactory.Instance.CreateLogger<VerbaraServer>());

        _sut.AddExistingServer("dup", server1);

        var act = () => _sut.AddExistingServer("dup", server2);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public void AddExistingServer_ShouldSubscribeToAgentEvents()
    {
        var conn = Substitute.For<IAmiConnection>();
        conn.AsteriskVersion.Returns("20.0.0");
        conn.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        var server = new VerbaraServer(conn, NullLoggerFactory.Instance.CreateLogger<VerbaraServer>());

        _sut.AddExistingServer("failover-2", server);

        // Agent login after adding should be tracked
        server.Agents.OnAgentLogin("Agent/5001");
        _sut.GetServerForAgent("Agent/5001").Should().BeSameAs(server);
    }
}
