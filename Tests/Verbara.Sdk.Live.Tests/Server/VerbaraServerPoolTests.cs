using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Verbara.Sdk;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerbaraServerPoolTests : IAsyncLifetime
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

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _sut.DisposeAsync();
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

    // ── An unusable reconnect value never reaches a pool connection ────────────────────────────────

    /// <summary>
    /// The pool builds its connection through the real <see cref="AmiConnectionFactory"/>, and no options validator runs
    /// on that path, so the connection's constructor is what rejects a maximum delay below the initial one. The socket
    /// factory refuses to hand out a socket: a connection that reached its connect would surface that refusal instead.
    /// </summary>
    [Fact]
    public async Task AddServerAsync_ShouldThrowNamingTheOption_WhenAReconnectValueIsUnusable()
    {
        var sockets = Substitute.For<ISocketConnectionFactory>();
        sockets.Create().Returns(_ => throw new InvalidOperationException("No socket in this test: the connection must not get as far as its connect."));
        await using var pool = new VerbaraServerPool(new AmiConnectionFactory(sockets, NullLoggerFactory.Instance), NullLoggerFactory.Instance);
        var options = new AmiConnectionOptions
        {
            Username = "admin",
            Password = "secret",
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromSeconds(1),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(500),
        };

        var act = async () => await pool.AddServerAsync("unusable", options);

        var thrown = await act.Should().ThrowAsync<ArgumentOutOfRangeException>(
            "with AutoReconnect on, a maximum delay below the initial one is a value the reconnect backoff cannot use");
        using (new AssertionScope())
        {
            thrown.Which.ParamName.Should().Be(nameof(AmiConnectionOptions.ReconnectMaxDelay), "the error names the option to fix");
            pool.Servers.Should().NotContain(entry => entry.Key == "unusable", "the pool holds no server under that id");
            sockets.ReceivedCalls().Should().BeEmpty("the connection is rejected before it asks for a socket");
        }
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

    /// <summary>
    /// The race the id check before the connect cannot close: two adds of the same id both find it free, the first is
    /// held in its connect while the second enters the pool, and the first then finds the id taken. Its connection was
    /// created for it and never reaches the caller, so the pool releases it; the held server keeps its own.
    /// </summary>
    [Fact]
    public async Task AddServerAsync_ShouldDisposeTheNewConnection_WhenAnAddOfTheSameIdWonTheRace()
    {
        var held = new TaskCompletionSource<IAmiConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new List<IAmiConnection>();
        var factory = Substitute.For<IAmiConnectionFactory>();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        factory.CreateAndConnectAsync(Arg.Any<AmiConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var conn = NewConnection();
                connections.Add(conn);
                // The first add's connect is held until the second add has entered the pool.
                if (connections.Count == 1)
                {
                    return new ValueTask<IAmiConnection>(held.Task);
                }
                return new ValueTask<IAmiConnection>(conn);
            });
#pragma warning restore CA2012
        await using var pool = new VerbaraServerPool(factory, NullLoggerFactory.Instance);

        var first = pool.AddServerAsync("dup-server", Credentials()).AsTask();
        var second = await pool.AddServerAsync("dup-server", Credentials());
        held.SetResult(connections[0]);
        var act = async () => await first;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        pool.GetServer("dup-server").Should().BeSameAs(second, "the add that entered the pool first keeps the id");
        // The loser's connection was created for it and never reaches the caller: the pool releases it.
        await connections[0].Received(1).DisposeAsync();
        // The server the pool holds under that id keeps its connection.
        await connections[1].DidNotReceive().DisposeAsync();
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
        var server = await _sut.AddServerAsync("routed", options);
        server.Agents.OnAgentLogin("Agent/3001");
        _sut.GetServerForAgent("Agent/3001").Should().BeSameAs(server, "the login routed the agent to the server");

        await _sut.RemoveServerAsync("routed");

        _sut.GetServerForAgent("Agent/3001").Should().BeNull("the removed server's routes are gone");
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

    // ── A duplicate id is refused before any connection is made ────────────────────────────────────────

    [Fact]
    public async Task AddServerAsync_ShouldAskForNoConnection_WhenTheIdIsAlreadyInThePool()
    {
        await _sut.AddServerAsync("dup-server", Credentials());
        var held = _sut.GetServer("dup-server");

        var act = async () => await _sut.AddServerAsync("dup-server", Credentials());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        using (new AssertionScope())
        {
            _created.Should().ContainSingle("a duplicate id is refused before the pool creates, dials or logs in a connection");
            await _connectionFactory.Received(1).CreateAndConnectAsync(Arg.Any<AmiConnectionOptions>(), Arg.Any<CancellationToken>());
            _sut.GetServer("dup-server").Should().BeSameAs(held, "the server the pool holds under that id is untouched");
            await _created[0].DidNotReceive().DisposeAsync();
        }
    }

    // ── A server whose start failed does not stay in the pool ──────────────────────────────────────────

    [Fact]
    public async Task AddServerAsync_ShouldLeaveNothingInThePool_WhenTheStartIsCancelledDuringTheLoad()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = LoadingConnection(ct => HangUntilCancelledAsync(loadStarted, ct));
        var (factory, created) = FactoryOf(failing, NewConnection());
        await using var pool = new VerbaraServerPool(factory, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource();

        var add = pool.AddServerAsync("s1", Credentials(), cts.Token).AsTask();
        await loadStarted.Task.WaitAsync(CleanupBound);
        await cts.CancelAsync();
        var act = async () => await add;

        await act.Should().ThrowAsync<OperationCanceledException>("the start's exception reaches the caller unchanged");
        using (new AssertionScope())
        {
            pool.ServerCount.Should().Be(0, "a server whose start failed is not kept");
            pool.GetServer("s1").Should().BeNull();
            await failing.Received(1).DisposeAsync();
        }

        var retry = await pool.AddServerAsync("s1", Credentials());
        pool.GetServer("s1").Should().BeSameAs(retry, "the id is free again after a failed start");
        created.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(AmiConnectionState.Initial)]
    [InlineData(AmiConnectionState.Disconnected)]
    public async Task AddServerAsync_ShouldLeaveNothingInThePool_WhenTheSessionEndsUnderTheLoadForGood(AmiConnectionState state)
    {
        // Initial or Disconnected, never Connected: a Connected connection whose load fails this way makes the start
        // return, because the reconnect will reload (the negative control below).
        var failing = LoadingConnection(_ => ThrowNotConnectedAsync(), state);
        var (factory, _) = FactoryOf(failing, NewConnection());
        await using var pool = new VerbaraServerPool(factory, NullLoggerFactory.Instance);

        var act = async () => await pool.AddServerAsync("s1", Credentials());

        await act.Should().ThrowAsync<AmiNotConnectedException>("the start's exception reaches the caller unchanged");
        using (new AssertionScope())
        {
            pool.ServerCount.Should().Be(0, "a server whose start failed is not kept");
            pool.GetServer("s1").Should().BeNull();
            await failing.Received(1).DisposeAsync();
        }

        var retry = await pool.AddServerAsync("s1", Credentials());
        pool.GetServer("s1").Should().BeSameAs(retry, "the id is free again after a failed start");
    }

    [Fact]
    public async Task AddServerAsync_ShouldDropTheRoutesTheLoadRecorded_WhenTheStartFails()
    {
        IObserver<ManagerEvent>? observer = null;
        var failing = LoadingConnection(_ =>
        {
            // A live agent login dispatched during the load, before the load's session ends for good.
            observer!.OnNext(new AgentLoginEvent { Agent = "Agent/1001", Channel = "PJSIP/1001-00000001" });
            return ThrowNotConnectedAsync();
        });
        failing.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(ci =>
        {
            observer = ci.Arg<IObserver<ManagerEvent>>();
            return Substitute.For<IDisposable>();
        });
        var (factory, _) = FactoryOf(failing);
        await using var pool = new VerbaraServerPool(factory, NullLoggerFactory.Instance);

        var act = async () => await pool.AddServerAsync("s1", Credentials());

        await act.Should().ThrowAsync<AmiNotConnectedException>();
        pool.GetServerForAgent("Agent/1001").Should().BeNull("a failed start leaves no route to the server it dropped");
        pool.ServerCount.Should().Be(0);
    }

    [Fact]
    public async Task AddServerAsync_ShouldThrowTheStartsException_WhenTheConnectionThenFailsToDispose()
    {
        var failing = LoadingConnection(_ => ThrowNotConnectedAsync());
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        failing.DisposeAsync().Returns(_ => throw new InvalidOperationException("connection dispose failed"));
#pragma warning restore CA2012
        var (factory, _) = FactoryOf(failing);
        var logs = new RecordingLoggerFactory();
        // Not disposed at the end: on the unfixed pool the failed server stays, and the pool's disposal would throw the
        // connection's failure over this test's own assertions.
        var pool = new VerbaraServerPool(factory, logs);

        var act = async () => await pool.AddServerAsync("s1", Credentials());

        await act.Should().ThrowAsync<AmiNotConnectedException>("a disposal failure does not hide why the start failed");
        using (new AssertionScope())
        {
            pool.ServerCount.Should().Be(0);
            await failing.Received(1).DisposeAsync();
            logs.PoolErrors.Should().ContainSingle()
                .Which.Message.Should().Be("[POOL] AMI connection dispose failed: server=s1");
        }
    }

    /// <summary>
    /// Negative control, green before and after: a connection reading <c>Connected</c> when its session ends under the
    /// load is reconnecting, the start returns, and the reload that follows the reconnect loads the state.
    /// </summary>
    [Fact]
    public async Task AddServerAsync_ShouldKeepTheServer_WhenTheReconnectWillRepairTheStart()
    {
        var reconnecting = LoadingConnection(_ => ThrowNotConnectedAsync(), AmiConnectionState.Connected);
        var (factory, _) = FactoryOf(reconnecting);
        await using var pool = new VerbaraServerPool(factory, NullLoggerFactory.Instance);

        var server = await pool.AddServerAsync("s1", Credentials());

        pool.GetServer("s1").Should().BeSameAs(server);
        await reconnecting.DidNotReceive().DisposeAsync();
    }

    /// <summary>
    /// The removal took the server out while it was starting and released it; the add's failure must not release it a
    /// second time.
    /// </summary>
    [Fact]
    public async Task AddServerAsync_ShouldNotDisposeAgain_WhenTheServerWasRemovedWhileItStarted()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = LoadingConnection(_ => ThrowNotConnectedAfterAsync(loadStarted, gate.Task));
        var (factory, _) = FactoryOf(failing);
        await using var pool = new VerbaraServerPool(factory, NullLoggerFactory.Instance);
        var ari = Substitute.For<IAriClient>();

        var add = pool.AddServerAsync("s1", Credentials()).AsTask();
        await loadStarted.Task.WaitAsync(CleanupBound);
        pool.GetServer("s1")!.SetAriClient(ari);
        await pool.RemoveServerAsync("s1");
        gate.SetResult();
        var act = async () => await add;

        await act.Should().ThrowAsync<AmiNotConnectedException>();
        using (new AssertionScope())
        {
            await failing.Received(1).DisposeAsync();
            await ari.Received(1).DisposeAsync();
        }
    }

    // ── Removing a server releases its connection even when a disposal fails ───────────────────────────

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RemoveServerAsync_ShouldReleaseTheConnectionAndNotThrow_WhenADisposalFails(bool serverThrows, bool connectionThrows)
    {
        var logs = new RecordingLoggerFactory();
        await using var pool = new VerbaraServerPool(_connectionFactory, logs);
        var connection = NewConnection();
        var server = HandedInServer(connection);
        var ari = Substitute.For<IAriClient>();
        server.SetAriClient(ari);
        pool.AddExistingServer("s1", server);
        server.Agents.OnAgentLogin("Agent/1001");
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        if (serverThrows)
            ari.DisposeAsync().Returns(_ => throw new InvalidOperationException("ari-s1"));
        if (connectionThrows)
            connection.DisposeAsync().Returns(_ => throw new InvalidOperationException("conn-s1"));
#pragma warning restore CA2012

        var act = async () => await pool.RemoveServerAsync("s1");

        await act.Should().NotThrowAsync("a removal never throws what the server or its connection threw");
        using (new AssertionScope())
        {
            await ari.Received(1).DisposeAsync();
            await connection.Received(1).DisposeAsync();
            pool.GetServer("s1").Should().BeNull();
            pool.GetServerForAgent("Agent/1001").Should().BeNull("the removed server's routes are gone");
            var expected = new List<string>();
            if (serverThrows)
                expected.Add("[POOL] Server dispose failed: server=s1");
            if (connectionThrows)
                expected.Add("[POOL] AMI connection dispose failed: server=s1");
            logs.PoolErrors.Select(e => e.Message).Should().Equal(expected);
            logs.PoolErrors.Should().OnlyContain(e => e.Exception is InvalidOperationException);
        }
    }

    [Fact]
    public async Task RemoveServerAsync_ShouldLeaveAnotherServersRoute_WhenTheSameAgentIdMovedThere()
    {
        await using var pool = new VerbaraServerPool(_connectionFactory, NullLoggerFactory.Instance);
        var s1 = HandedInServer(NewConnection());
        var s2 = HandedInServer(NewConnection());
        pool.AddExistingServer("s1", s1);
        pool.AddExistingServer("s2", s2);
        s1.Agents.OnAgentLogin("Agent/1001");
        s2.Agents.OnAgentLogin("Agent/1001");
        s1.Agents.OnAgentLogin("Agent/2002");

        await pool.RemoveServerAsync("s1");

        using (new AssertionScope())
        {
            pool.GetServerForAgent("Agent/1001").Should().BeSameAs(s2, "the route another server owns survives");
            pool.GetServerForAgent("Agent/2002").Should().BeNull("a route only the removed server owned is gone");
        }
    }

    // ── Disposing the pool does not throw, logs each failure, and does nothing the second time ───────

    /// <summary>One server of three throws when disposed, at each position the pool disposes them in.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DisposeAsync_ShouldReleaseEveryServerAndNotThrow_WhenOneServerFailsToDispose(int failingPosition)
    {
        var (pool, logs, ids, connections, aris) = PoolOfThree();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        aris[failingPosition].DisposeAsync().Returns(_ => throw new InvalidOperationException($"ari-{ids[failingPosition]}"));
#pragma warning restore CA2012

        var act = async () => await pool.DisposeAsync();

        await act.Should().NotThrowAsync("the pool's disposal never throws what a server threw");
        using (new AssertionScope())
        {
            foreach (var connection in connections)
                await connection.Received(1).DisposeAsync();
            foreach (var ari in aris)
                await ari.Received(1).DisposeAsync();
            pool.ServerCount.Should().Be(0);
            logs.PoolErrors.Select(e => e.Message).Should().Equal($"[POOL] Server dispose failed: server={ids[failingPosition]}");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReleaseEveryServerAndNotThrow_WhenTwoConnectionsFailToDispose()
    {
        var (pool, logs, ids, connections, aris) = PoolOfThree();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        connections[0].DisposeAsync().Returns(_ => throw new InvalidOperationException($"conn-{ids[0]}"));
        connections[2].DisposeAsync().Returns(_ => throw new InvalidOperationException($"conn-{ids[2]}"));
#pragma warning restore CA2012

        var act = async () => await pool.DisposeAsync();

        await act.Should().NotThrowAsync();
        using (new AssertionScope())
        {
            foreach (var connection in connections)
                await connection.Received(1).DisposeAsync();
            foreach (var ari in aris)
                await ari.Received(1).DisposeAsync();
            pool.ServerCount.Should().Be(0);
            logs.PoolErrors.Select(e => e.Message).Should().Equal(
                $"[POOL] AMI connection dispose failed: server={ids[0]}",
                $"[POOL] AMI connection dispose failed: server={ids[2]}");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReleaseEveryServerAndNotThrow_WhenEveryServerFailsToDispose()
    {
        var (pool, logs, ids, connections, aris) = PoolOfThree();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        for (var i = 0; i < aris.Count; i++)
        {
            var id = ids[i];
            aris[i].DisposeAsync().Returns(_ => throw new InvalidOperationException($"ari-{id}"));
        }
#pragma warning restore CA2012

        var act = async () => await pool.DisposeAsync();

        await act.Should().NotThrowAsync();
        using (new AssertionScope())
        {
            foreach (var connection in connections)
                await connection.Received(1).DisposeAsync();
            pool.ServerCount.Should().Be(0);
            logs.PoolErrors.Select(e => e.Message).Should().Equal(ids.Select(id => $"[POOL] Server dispose failed: server={id}"));
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldDoNothing_WhenCalledASecondTime()
    {
        var (pool, logs, ids, connections, aris) = PoolOfThree();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        aris[0].DisposeAsync().Returns(_ => throw new InvalidOperationException($"ari-{ids[0]}"));
#pragma warning restore CA2012
        try
        {
            await pool.DisposeAsync();
        }
        catch (InvalidOperationException)
        {
            // Today's first disposal throws; the second is what this test measures.
        }
        var entriesAfterFirst = logs.Entries.Count;

        var act = async () => await pool.DisposeAsync();

        await act.Should().NotThrowAsync("a second disposal finds nothing to dispose");
        using (new AssertionScope())
        {
            foreach (var connection in connections)
                await connection.Received(1).DisposeAsync();
            foreach (var ari in aris)
                await ari.Received(1).DisposeAsync();
            logs.Entries.Should().HaveCount(entriesAfterFirst, "a second disposal logs nothing");
        }
    }

    // ── Fixtures for the failure paths ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pool of its own (not <c>_sut</c>, whose class cleanup would fail a second time over the same throwing server)
    /// holding three handed-in servers, each with an ARI client; ids in the order the pool disposes them.
    /// </summary>
    private static (VerbaraServerPool Pool, RecordingLoggerFactory Logs, List<string> Ids, List<IAmiConnection> Connections, List<IAriClient> Aris) PoolOfThree()
    {
        var logs = new RecordingLoggerFactory();
        var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), logs);
        var byId = new Dictionary<string, (IAmiConnection Connection, IAriClient Ari)>();
        foreach (var id in new[] { "s1", "s2", "s3" })
        {
            var connection = NewConnection();
            var server = HandedInServer(connection);
            var ari = Substitute.For<IAriClient>();
            server.SetAriClient(ari);
            pool.AddExistingServer(id, server);
            byId[id] = (connection, ari);
        }

        var ids = pool.Servers.Select(entry => entry.Key).ToList();
        return (pool, logs, ids, ids.Select(id => byId[id].Connection).ToList(), ids.Select(id => byId[id].Ari).ToList());
    }

    /// <summary>A connection whose state load runs <paramref name="load"/> for every event-generating request.</summary>
    private static IAmiConnection LoadingConnection(
        Func<CancellationToken, IAsyncEnumerable<ManagerEvent>> load,
        AmiConnectionState state = AmiConnectionState.Initial)
    {
        var conn = Substitute.For<IAmiConnection>();
        conn.State.Returns(state);
        conn.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        conn.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => load(ci.ArgAt<CancellationToken>(1)));
        return conn;
    }

    /// <summary>A factory that hands out <paramref name="connections"/> in order, recording each.</summary>
    private static (IAmiConnectionFactory Factory, List<IAmiConnection> Created) FactoryOf(params IAmiConnection[] connections)
    {
        var queue = new Queue<IAmiConnection>(connections);
        var created = new List<IAmiConnection>();
        var factory = Substitute.For<IAmiConnectionFactory>();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        factory.CreateAndConnectAsync(Arg.Any<AmiConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var conn = queue.Dequeue();
                created.Add(conn);
                return new ValueTask<IAmiConnection>(conn);
            });
#pragma warning restore CA2012
        return (factory, created);
    }

    private static async IAsyncEnumerable<ManagerEvent> HangUntilCancelledAsync(
        TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        // A load the peer never answers: only the caller's token ends it.
        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<ManagerEvent> ThrowNotConnectedAsync()
    {
        await Task.Yield();
        throw new AmiNotConnectedException("Not connected: the load's session ended for good");
#pragma warning disable CS0162 // Unreachable code: an iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ManagerEvent> ThrowNotConnectedAfterAsync(TaskCompletionSource started, Task gate)
    {
        started.TrySetResult();
        await gate;
        throw new AmiNotConnectedException("Not connected: the load's session ended for good");
#pragma warning disable CS0162 // Unreachable code: an iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>A logger factory that records every entry its loggers write, with its level and formatted message.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        /// <summary>The Error entries the pool itself wrote.</summary>
        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> PoolErrors =>
            [.. Entries.Where(e => e.Level == LogLevel.Error && e.Message.StartsWith("[POOL]", StringComparison.Ordinal))];

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Recorder(RecordingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                    owner._entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
