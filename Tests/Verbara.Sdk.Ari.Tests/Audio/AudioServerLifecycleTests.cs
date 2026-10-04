using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// Disposal, failed starts, restarts and the registration window of the two ARI audio servers. The
/// registration window is reached through the servers' internal seams, so a stop lands exactly there.
/// </summary>
public sealed class AudioServerLifecycleTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    public enum Kind
    {
        AudioSocket,
        WebSocket,
    }

    // ------------------------------------------------------------- second disposal

    [Theory]
    [InlineData(Kind.AudioSocket)]
    [InlineData(Kind.WebSocket)]
    public async Task DisposeAsync_ShouldNotThrowAndCompleteAnnouncementsOnce_WhenAStartedServerIsDisposedTwice(Kind kind)
    {
        // Arrange
        var server = await StartedAsync(kind);
        var completions = 0;
        using var subscription = server.Announcements.Subscribe(static _ => { }, () => Interlocked.Increment(ref completions));
        await server.Dispose();

        // Act
        var second = await Record.ExceptionAsync(async () => await server.Dispose());

        // Assert
        using (new AssertionScope())
        {
            second.Should().BeNull("every disposal after the first is a no-op");
            Volatile.Read(ref completions).Should().Be(1, "the announcements complete once");
        }
    }

    [Theory]
    [InlineData(Kind.AudioSocket)]
    [InlineData(Kind.WebSocket)]
    public async Task DisposeAsync_ShouldNotThrow_WhenANeverStartedServerIsDisposedTwice(Kind kind)
    {
        // Arrange
        var server = Create(kind, port: 0);
        await server.Dispose();

        // Act
        var second = await Record.ExceptionAsync(async () => await server.Dispose());

        // Assert
        second.Should().BeNull("a server never started is disposed twice without an exception");
    }

    // ------------------------------------------------------------------- failed start

    [Theory]
    [InlineData(Kind.AudioSocket)]
    [InlineData(Kind.WebSocket)]
    public async Task StartAsync_ShouldLeaveTheServerStoppedAndStartable_WhenItsPortIsBusy(Kind kind)
    {
        // Arrange — another listener holds the port
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        var server = Create(kind, port);
        bool runningAfterFailure;
        Exception? first;
        Exception? second;
        bool accepts;
        try
        {
            // Act
            first = await Record.ExceptionAsync(async () => await server.Start());
            runningAfterFailure = server.IsRunning();
            busy.Stop();
            second = await Record.ExceptionAsync(async () => await server.Start());
            accepts = await AcceptsAsync(port);
        }
        finally
        {
            await server.Dispose();
        }

        // Assert
        using (new AssertionScope())
        {
            first.Should().BeOfType<SocketException>("the bind failed");
            runningAfterFailure.Should().BeFalse("a start whose bind failed leaves the server stopped");
            second.Should().BeNull();
            accepts.Should().BeTrue("the next start, once the port is free, binds and accepts");
        }
    }

    [Theory]
    [InlineData(Kind.AudioSocket)]
    [InlineData(Kind.WebSocket)]
    public async Task StartAsync_ShouldLeaveTheServerStoppedAndStartable_WhenItsListenAddressIsInvalid(Kind kind)
    {
        // Arrange
        var server = Create(kind, port: LoopbackServerBind.ProbePorts(1)[0], listenAddress: "not-an-address");
        bool runningAfterFailure;
        Exception? first;
        Exception? second;
        bool accepts;
        try
        {
            // Act
            first = await Record.ExceptionAsync(async () => await server.Start());
            runningAfterFailure = server.IsRunning();
            server.Options.ListenAddress = "127.0.0.1";
            second = await Record.ExceptionAsync(async () => await server.Start());
            accepts = await AcceptsAsync(server.Port);
        }
        finally
        {
            await server.Dispose();
        }

        // Assert
        using (new AssertionScope())
        {
            first.Should().BeOfType<FormatException>();
            runningAfterFailure.Should().BeFalse("a start that threw leaves the server stopped");
            second.Should().BeNull();
            accepts.Should().BeTrue("a start with a valid address binds and accepts");
        }
    }

    // ------------------------------------------------------------------- restart

    [Fact]
    public async Task StartAsync_ShouldBindAndServe_WhenTheAudioSocketServerWasStopped()
    {
        // A pin of the ARI contract, green before and after: unlike the Voice AI server, an ARI audio
        // server binds and serves again when started after a stop
        await using var harness = await AudioSocketEndingHarness.StartAsync(_ => { });
        using var bound = new CancellationTokenSource(SignalTimeout);
        await harness.Server.StopAsync();

        await harness.Server.StartAsync();
        var (peer, recording) = await harness.IdentifyUntilAdmittedAsync(Guid.NewGuid(), bound.Token);
        using var _ = peer;

        using (new AssertionScope())
        {
            harness.Server.IsRunning.Should().BeTrue();
            recording.Stream.IsConnected.Should().BeTrue("the restarted server announced the identified peer");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldBindAndServe_WhenTheWebSocketServerWasStopped()
    {
        await using var harness = await WebSocketEndingHarness.StartAsync(_ => { });
        using var bound = new CancellationTokenSource(SignalTimeout);
        await harness.Server.StopAsync();

        await harness.Server.StartAsync();
        var (peer, _) = await harness.ConnectUntilAdmittedAsync($"restart-{Guid.NewGuid():N}", bound.Token);
        using var connection = peer;

        harness.Server.IsRunning.Should().BeTrue("the restarted server accepted and announced the connection");
    }

    // ------------------------------------------------------------ the stop window

    [Fact]
    public async Task HandleConnection_ShouldNotAnnounce_WhenTheStopCompletesBeforeTheRegistration()
    {
        // Arrange — port of the Voice AI server's stop-window test: the stop runs to completion between the
        // identification and the registration
        await using var harness = await AudioSocketEndingHarness.StartWithReleaseSignalsAsync(_ => { }, maxStreams: 1);
        using var bound = new CancellationTokenSource(SignalTimeout);
        var server = harness.Server;
        server.BeforeRegistration = Once(() => server.StopAsync().AsTask().GetAwaiter().GetResult());
        var listedInTheWindow = -1;
        server.AfterRegistryAdd = Once(() => listedInTheWindow = server.ActiveStreamCount);

        // Act
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
        await peer.SendAsync(AudioSocketFrames.Uuid(Guid.NewGuid()), bound.Token);
        var accepted = await harness.NextAcceptedAsync(bound.Token);
        await accepted.Released.WaitAsync(bound.Token);
        await peer.Closed.WaitAsync(bound.Token);
        var announced = harness.Announcements;
        var active = harness.Server.ActiveStreamCount;
        var listed = listedInTheWindow;
        server.AfterRegistryAdd = null;
        var held = await HeldAfterRestartAsync(harness, bound.Token);

        // Assert
        using (new AssertionScope())
        {
            announced.Should().Be(0, "a connection that registers once the stop has begun is never announced");
            active.Should().Be(0);
            held.Should().Be(0, "its place was given back");
            harness.Logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
            listed.Should().Be(-1, "a refused connection is never added to the tables, not even for a moment");
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldLogNoError_WhenTheServerIsDisposedBeforeTheRegistration()
    {
        // Arrange
        await using var harness = await AudioSocketEndingHarness.StartWithReleaseSignalsAsync(_ => { });
        harness.ServerDisposedByTest = true;
        using var bound = new CancellationTokenSource(SignalTimeout);
        var server = harness.Server;
        server.BeforeRegistration = Once(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult());

        // Act
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
        await peer.SendAsync(AudioSocketFrames.Uuid(Guid.NewGuid()), bound.Token);
        var accepted = await harness.NextAcceptedAsync(bound.Token);
        await accepted.Released.WaitAsync(bound.Token);
        await peer.Closed.WaitAsync(bound.Token);

        // The release is the handler's last step, after any connection error it logs

        // Assert
        using (new AssertionScope())
        {
            harness.Announcements.Should().Be(0);
            harness.Logger.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => $"{e.EventName}({e.Exception?.GetType().Name})")
                .Should().BeEmpty("a disposal in the registration window is the stop, not a connection error");
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldNotAnnounceOnTheRestartedServer_WhenTheServerRestartsBeforeTheRegistration()
    {
        // Arrange
        await using var harness = await AudioSocketEndingHarness.StartWithReleaseSignalsAsync(_ => { });
        using var bound = new CancellationTokenSource(SignalTimeout);
        var server = harness.Server;
        server.BeforeRegistration = Once(() =>
        {
            server.StopAsync().AsTask().GetAwaiter().GetResult();
            server.StartAsync().AsTask().GetAwaiter().GetResult();
        });

        // Act
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
        await peer.SendAsync(AudioSocketFrames.Uuid(Guid.NewGuid()), bound.Token);
        var accepted = await harness.NextAcceptedAsync(bound.Token);
        await accepted.Released.WaitAsync(bound.Token);
        await peer.Closed.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            harness.Announcements.Should().Be(0, "a connection from before the stop is not adopted by the restarted server");
            harness.Server.ActiveStreamCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldWithdrawTheEntry_WhenTheStopRunsBetweenTheAddAndItsCheck()
    {
        // Arrange — port of the Voice AI server's second stop-window test: the stop runs right after the add
        await using var harness = await AudioSocketEndingHarness.StartWithReleaseSignalsAsync(_ => { }, maxStreams: 1);
        using var bound = new CancellationTokenSource(SignalTimeout);
        var server = harness.Server;
        server.AfterRegistryAdd = Once(() => server.StopAsync().AsTask().GetAwaiter().GetResult());
        var id = Guid.NewGuid();

        // Act
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
        await peer.SendAsync(AudioSocketFrames.Uuid(id), bound.Token);
        var accepted = await harness.NextAcceptedAsync(bound.Token);
        await accepted.Released.WaitAsync(bound.Token);
        await peer.Closed.WaitAsync(bound.Token);
        var announced = harness.Announcements;
        var active = harness.Server.ActiveStreamCount;
        var found = harness.Server.GetStream(id.ToString());
        var held = await HeldAfterRestartAsync(harness, bound.Token);

        // Assert
        using (new AssertionScope())
        {
            announced.Should().Be(0, "the entry the stop's walk claimed is withdrawn, never announced");
            active.Should().Be(0);
            found.Should().BeNull();
            held.Should().Be(0);
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldNotAnnounce_WhenTheWebSocketStopBeginsBeforeTheRegistration()
    {
        // Arrange — the WebSocket stop waits for its handlers, so the seam only begins it
        await using var harness = await WebSocketEndingHarness.StartAsync(_ => { });
        using var bound = new CancellationTokenSource(SignalTimeout);
        var stopBegun = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = harness.Server;
        server.BeforeRegistration = Once(() => stopBegun.TrySetResult(server.StopAsync().AsTask()));
        var listedInTheWindow = -1;
        server.AfterRegistryAdd = Once(() => listedInTheWindow = server.ActiveStreamCount);

        // Act
        using var peer = await WebSocketTestPeer.ConnectAsync(harness.Port, $"stop-{Guid.NewGuid():N}", bound.Token);
        var stop = await stopBegun.Task.WaitAsync(bound.Token);
        await stop.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            harness.Announcements.Should().Be(0, "a connection that registers during the stop is refused");
            harness.Server.ActiveStreamCount.Should().Be(0);
            listedInTheWindow.Should().Be(-1, "a refused connection is never added to the tables, not even for a moment");
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldNotAnnounce_WhenTheWebSocketStopBeginsBetweenTheAddAndItsCheck()
    {
        // Arrange — the stop begins right after the add; the WebSocket stop waits for its handlers
        await using var harness = await WebSocketEndingHarness.StartAsync(_ => { });
        using var bound = new CancellationTokenSource(SignalTimeout);
        var stopBegun = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = harness.Server;
        server.AfterRegistryAdd = Once(() => stopBegun.TrySetResult(server.StopAsync().AsTask()));

        // Act
        using var peer = await WebSocketTestPeer.ConnectAsync(harness.Port, $"add-{Guid.NewGuid():N}", bound.Token);
        var stop = await stopBegun.Task.WaitAsync(bound.Token);
        await stop.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            harness.Announcements.Should().Be(0, "the entry added once the stop had begun is withdrawn, never announced");
            harness.Server.ActiveStreamCount.Should().Be(0);
        }
    }


    // -------------------------------------------------------------------------------- helpers

    /// <summary>A seam body that acts on the first call only, so later connections pass the seam untouched.</summary>
    private static Action Once(Action act)
    {
        var once = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
                act();
        };
    }

    /// <summary>
    /// The places a stopped server still holds, proven by admission: restarted under a limit of one, it
    /// must announce one identifying peer; the count is read after that announcement, minus the probe.
    /// </summary>
    private static async Task<int> HeldAfterRestartAsync(AudioSocketEndingHarness harness, CancellationToken token)
    {
        await harness.Server.StartAsync(token);
        var (probe, _) = await harness.IdentifyUntilAdmittedAsync(Guid.NewGuid(), token);
        using (probe)
            return harness.Options.Admission.Held - 1;
    }

    private static async Task<bool> AcceptsAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SignalTimeout);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<ServerUnderTest> StartedAsync(Kind kind)
    {
        var (server, _) = await LoopbackServerBind.StartAsync(p => Create(kind, p), s => s.Start());
        return server;
    }

    private static ServerUnderTest Create(Kind kind, int port, string listenAddress = "127.0.0.1")
    {
        var options = new AudioServerOptions { ListenAddress = listenAddress, AudioSocketPort = port, WebSocketPort = port };
        if (kind == Kind.AudioSocket)
        {
            var server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
            return new ServerUnderTest(options, port, server.OnStreamConnected, () => server.StartAsync(), () => server.IsRunning, () => server.DisposeAsync());
        }

        var ws = new WebSocketAudioServer(options, NullLogger<WebSocketAudioServer>.Instance);
        return new ServerUnderTest(options, port, ws.OnStreamConnected, () => ws.StartAsync(), () => ws.IsRunning, () => ws.DisposeAsync());
    }

    /// <summary>One of the two servers, reduced to what these tests drive.</summary>
    private sealed class ServerUnderTest(
        AudioServerOptions options,
        int port,
        IObservable<IAudioStream> announcements,
        Func<ValueTask> start,
        Func<bool> isRunning,
        Func<ValueTask> dispose) : IAsyncDisposable
    {
        public AudioServerOptions Options { get; } = options;

        public int Port { get; } = port;

        public IObservable<IAudioStream> Announcements { get; } = announcements;

        public ValueTask Start() => start();

        public bool IsRunning() => isRunning();

        public ValueTask Dispose() => dispose();

        public ValueTask DisposeAsync() => dispose();
    }
}
