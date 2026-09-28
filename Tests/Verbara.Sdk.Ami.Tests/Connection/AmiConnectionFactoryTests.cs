using System.Net.Sockets;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Ami.Tests.Connection;

public sealed class AmiConnectionFactoryTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string ConnectRefused = "the peer refuses the connect";
    private const string NoProtocolIdentifier = "the peer's first message is not the protocol identifier";
    private const string LoginRejected = "the peer rejects the login";

    [Fact]
    public void Create_ShouldReturnAmiConnection()
    {
        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var sut = new AmiConnectionFactory(socketFactory, loggerFactory);
        var options = new AmiConnectionOptions
        {
            Hostname = "192.168.1.100",
            Port = 5038,
            Username = "admin",
            Password = "secret"
        };

        var connection = sut.Create(options);

        connection.Should().NotBeNull();
        connection.Should().BeOfType<AmiConnection>();
    }

    [Fact]
    public void Create_ShouldReturnIAmiConnection()
    {
        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var sut = new AmiConnectionFactory(socketFactory, loggerFactory);
        var options = new AmiConnectionOptions { Username = "user", Password = "pass" };

        var connection = sut.Create(options);

        connection.Should().BeAssignableTo<IAmiConnection>();
    }

    [Fact]
    public void Create_ShouldAcceptDifferentOptions()
    {
        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var sut = new AmiConnectionFactory(socketFactory, loggerFactory);

        var conn1 = sut.Create(new AmiConnectionOptions { Hostname = "server1", Username = "u1", Password = "p1" });
        var conn2 = sut.Create(new AmiConnectionOptions { Hostname = "server2", Username = "u2", Password = "p2" });

        conn1.Should().NotBeSameAs(conn2);
    }

    // ── A connection whose connect fails is released before the error reaches the caller ──────────────

    /// <summary>
    /// <see cref="AmiConnectionFactory.CreateAndConnectAsync"/> returns no connection when its
    /// <c>ConnectAsync</c> throws, so its caller can never dispose it: the factory releases it itself, and
    /// then lets the original error through unchanged. Read the moment the error reaches the caller, every
    /// socket the connection created has been disposed exactly once, and the peer sees its socket closed.
    /// </summary>
    /// <remarks>
    /// The rejected login is the shape measured against a real listener: one socket left in
    /// <c>CLOSE_WAIT</c> and a connection that read <c>Connecting</c> for the rest of the run. Each peer is an
    /// in-memory <see cref="PipedSocket"/>; every wait is bounded by <see cref="Bound"/> and ends on the signal
    /// it asserts.
    /// </remarks>
    [Theory]
    [InlineData(LoginRejected, typeof(AmiAuthenticationException), "LoginAsync")]
    [InlineData(NoProtocolIdentifier, typeof(AmiProtocolException), "ConnectCoreAsync")]
    [InlineData(ConnectRefused, typeof(SocketException), "ConnectCoreAsync")]
    public async Task CreateAndConnectAsync_ShouldReleaseTheConnectionBeforeRethrowing_WhenItsConnectFails(
        string failure, Type expected, string origin)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory { ConnectsAccepted = failure == ConnectRefused ? 0 : 1 };
        var sut = new AmiConnectionFactory(sockets, NullLoggerFactory.Instance);
        var peer = Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(peerCts.Token);
            await PlayFailureAsync(socket, failure, peerCts.Token);
            return socket;
        }, peerCts.Token);

        var connect = async () => await sut.CreateAndConnectAsync(ConnectOptions()).AsTask().WaitAsync(Bound);

        var thrown = await connect.Should().ThrowAsync<Exception>($"{failure}, so the connect fails");
        // Read at once: whatever releases the connection has run before the error reached this caller.
        var unreleased = Unreleased(sockets.Created);
        var socket = await peer.WaitAsync(Bound);
        var peerSeesItOpen = socket.IsConnected;

        using (new AssertionScope())
        {
            thrown.Which.Should().BeOfType(expected, "the error ConnectAsync raised reaches the caller unchanged");
            thrown.Which.StackTrace.Should().Contain(origin,
                "the error keeps the stack of the place it was raised, so the factory rethrows it rather than throwing it anew");
            unreleased.Should().BeEmpty(
                $"{failure}, and the factory, which never hands that connection to its caller, releases every socket it " +
                "created exactly once before the error reaches the caller");
            peerSeesItOpen.Should().BeFalse("the peer sees its socket closed");
            if (failure == LoginRejected)
                thrown.Which.Message.Should().Be("AMI login failed: Authentication failed", "it is the peer's rejection, unchanged");
        }
    }

    /// <summary>
    /// A caller who cancels <see cref="AmiConnectionFactory.CreateAndConnectAsync"/> gets the cancellation,
    /// not another error, and the connection the cancelled connect created is released before it arrives.
    /// </summary>
    [Fact]
    public async Task CreateAndConnectAsync_ShouldReleaseTheConnectionAndRethrowTheCancellation_WhenTheCallerCancels()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        using var callerCts = new CancellationTokenSource();
        var sockets = new PipedSocketFactory();
        var sut = new AmiConnectionFactory(sockets, NullLoggerFactory.Instance);

        // The peer withholds the banner, so the connect waits for it until the caller cancels.
        var connecting = sut.CreateAndConnectAsync(ConnectOptions(), callerCts.Token).AsTask();
        var socket = await sockets.NextAsync(peerCts.Token).AsTask().WaitAsync(Bound);
        await callerCts.CancelAsync();

        var connect = async () => await connecting.WaitAsync(Bound);

        await connect.Should().ThrowAsync<OperationCanceledException>("the caller cancelled the connect, and cancellation stays cancellation");
        // Read at once: whatever releases the connection has run before the cancellation reached this caller.
        var disposeCount = socket.DisposeCount;
        var peerSeesItOpen = socket.IsConnected;

        using (new AssertionScope())
        {
            disposeCount.Should().Be(1,
                "the factory, which never hands the cancelled connection to its caller, releases its socket exactly once before the cancellation reaches the caller");
            peerSeesItOpen.Should().BeFalse("the peer sees its socket closed");
            sockets.Created.Should().ContainSingle("the cancelled connect created one socket, and nothing dials another");
        }
    }

    /// <summary>
    /// A pin, green before and after the release above: a connect that succeeds hands its caller the connection,
    /// connected and with its socket open, and that connection is the caller's to dispose. Only a failed connect
    /// is released by the factory.
    /// </summary>
    [Fact]
    public async Task CreateAndConnectAsync_ShouldReturnTheConnectionConnectedAndUnreleased_WhenItsConnectSucceeds()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        var sut = new AmiConnectionFactory(sockets, NullLoggerFactory.Instance);
        var loggedIn = Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(peerCts.Token);
            await socket.CompleteLoginAsync(peerCts.Token);
            return socket;
        }, peerCts.Token);

        var connection = await sut.CreateAndConnectAsync(ConnectOptions()).AsTask().WaitAsync(Bound);
        var socket = await loggedIn.WaitAsync(Bound);

        using (new AssertionScope())
        {
            connection.State.Should().Be(AmiConnectionState.Connected, "the connect succeeded");
            socket.DisposeCount.Should().Be(0, "the factory releases only a connection whose connect failed");
            socket.IsConnected.Should().BeTrue("the peer sees its socket open");
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue("the caller's DisposeAsync returns");
        socket.DisposeCount.Should().Be(1, "the caller's DisposeAsync releases the socket exactly once");
    }

    private static AmiConnectionOptions ConnectOptions() => new()
    {
        Hostname = "localhost",
        Username = "admin",
        Password = "secret",
        EnableHeartbeat = false,
    };

    /// <summary>Plays the peer up to the point where the connection's connect fails.</summary>
    private static async Task PlayFailureAsync(PipedSocket peer, string failure, CancellationToken ct)
    {
        switch (failure)
        {
            case ConnectRefused:
                // The socket refused its connect; the connection wrote nothing to it.
                return;
            case NoProtocolIdentifier:
                await peer.RespondAsync("Error", "1");
                return;
            case LoginRejected:
                await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
                var challenge = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("No challenge.");
                await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
                var login = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("No login.");
                await peer.RespondAsync("Error", PipedSocket.ActionIdOf(login), [new("Message", "Authentication failed")]);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, "Not a failure this test plays.");
        }
    }

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Each socket not disposed exactly once, described by its place in creation order.</summary>
    private static List<string> Unreleased(IReadOnlyList<PipedSocket> sockets) =>
        [.. sockets
            .Select((socket, index) => (socket.DisposeCount, Place: index + 1))
            .Where(s => s.DisposeCount != 1)
            .Select(s => $"socket {s.Place} of {sockets.Count}: disposed {s.DisposeCount} time(s)")];
}
