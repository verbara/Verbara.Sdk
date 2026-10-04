using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Nats;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace Verbara.Sdk.Push.Nats.Tests;

/// <summary>
/// What a <see cref="NatsBridge"/> opens — its publisher, its subscriber and its subscription to the
/// Push bus — is released exactly once by whichever of a stop or a disposal comes first, including a
/// connection a factory hands over after the stop returned; and the default connection factory closes a
/// connect it gave up on. Fakes count every factory call and every release, a bus counts its live
/// subscriptions, and a NATS-shaped loopback peer counts the sockets it still holds open.
/// </summary>
/// <remarks>
/// Every fence is causal: a factory signals that it was entered and parks on a gate the test opens; the
/// consume loop signals that it subscribed; the default factory's connect is fenced on the bridge's own
/// "connected" log line, never on the peer's first <c>PONG</c> (the client may not have processed it).
/// Every wait is bounded and its outcome is part of the asserted tuple, so a defect reads as a count.
/// </remarks>
public sealed class NatsBridgeReleaseTests
{
    private static readonly TimeSpan Fence = TimeSpan.FromSeconds(5);

    // ---------------------------------------------------------------- a stop

    [Fact]
    public async Task StopAsync_ShouldReleaseThePublisherAndOpenNothingMore_WhenThePublisherArrivesAfterAnExpiredStop()
    {
        var rig = new Rig(parkPublisher: true);
        using var bridge = rig.Build(subscribe: true);

        await bridge.StartAsync(CancellationToken.None);
        await rig.PublisherEntered.WaitAsync(Fence);

        await bridge.StopAsync(new CancellationToken(canceled: true)); // the host's shutdown budget is spent
        rig.OpenPublisherGate(); // the factory hands its connection over after the stop returned
        var executeEnded = await EndsWithinAsync(bridge.ExecuteTask!, Fence);

        (executeEnded, rig.Publisher.Disposals, rig.SubscriberFactoryCalls, rig.Bus.Live)
            .Should().Be(
                (true, 1, 0, 0),
                "a publisher handed over after the stop is released, and nothing more is opened");
    }

    [Fact]
    public async Task StopAsync_ShouldReleaseTheSubscriberAndThePublisher_WhenTheSubscriberArrivesAfterAnExpiredStop()
    {
        var rig = new Rig(parkSubscriber: true);
        using var bridge = rig.Build(subscribe: true);

        await bridge.StartAsync(CancellationToken.None);
        await rig.SubscriberEntered.WaitAsync(Fence);

        await bridge.StopAsync(new CancellationToken(canceled: true));
        rig.OpenSubscriberGate();
        var executeEnded = await EndsWithinAsync(bridge.ExecuteTask!, Fence);

        (executeEnded, rig.Publisher.Disposals, rig.Subscriber.Disposals, rig.Subscriber.Subscriptions, rig.Bus.Live)
            .Should().Be(
                (true, 1, 1, 0, 0),
                "a subscriber handed over after the stop is released and never consumed from");
    }

    [Fact]
    public async Task StopAsync_ShouldLeaveNoBusSubscription_WhenAGracefulStopWaitsForTheConnect()
    {
        var rig = new Rig(parkPublisher: true);
        using var bridge = rig.Build(subscribe: true);

        await bridge.StartAsync(CancellationToken.None);
        await rig.PublisherEntered.WaitAsync(Fence);

        var stop = bridge.StopAsync(CancellationToken.None); // waits for ExecuteAsync
        rig.OpenPublisherGate(); // the factory returns while the stop waits
        var stopEnded = await EndsWithinAsync(stop, Fence);

        (stopEnded, rig.Publisher.Disposals, rig.SubscriberFactoryCalls, rig.Bus.Live)
            .Should().Be(
                (true, 1, 0, 0),
                "the stop releases what the factory returned while it waited, and nothing more is opened");
    }

    [Fact]
    public async Task StopAsync_ShouldReleaseEverythingOnce_WhenTheBridgeIsConnected()
    {
        var rig = new Rig();
        using var bridge = rig.Build(subscribe: true);

        await bridge.StartAsync(CancellationToken.None);
        await rig.Subscriber.Subscribed.WaitAsync(Fence);

        await bridge.StopAsync(CancellationToken.None);

        (rig.Publisher.Disposals, rig.Subscriber.Disposals, rig.Bus.Live, rig.Log.Count("NATS bridge failed to dispose"))
            .Should().Be((1, 1, 0, 0), "a stop of a connected bridge releases each resource once");
    }

    // ---------------------------------------------------------------- a disposal

    [Fact]
    public async Task Dispose_ShouldReleaseEverythingOnce_WhenTheStartedBridgeWasNeverStopped()
    {
        var rig = new Rig();
        using var bridge = rig.Build(subscribe: true);

        await bridge.StartAsync(CancellationToken.None);
        await rig.Subscriber.Subscribed.WaitAsync(Fence);

        bridge.Dispose();
        var afterFirst = (rig.Publisher.Disposals, rig.Subscriber.Disposals, rig.Bus.Live);
        var second = () => bridge.Dispose(); // AddPushNats registers the bridge twice; the container disposes it twice
        second.Should().NotThrow();
        var executeEnded = await EndsWithinAsync(bridge.ExecuteTask!, Fence);

        (afterFirst, (rig.Publisher.Disposals, rig.Subscriber.Disposals, rig.Bus.Live), executeEnded)
            .Should().Be(
                ((1, 1, 0), (1, 1, 0), true),
                "a disposal releases what the bridge opened before it returns, and a second one releases nothing again");
    }

    [Fact]
    public async Task Dispose_ShouldReleaseNothingAgain_WhenTheBridgeWasStoppedAndIsDisposedTwice()
    {
        var rig = new Rig();
        using var bridge = rig.Build(subscribe: true);

        await bridge.StartAsync(CancellationToken.None);
        await rig.Subscriber.Subscribed.WaitAsync(Fence);
        await bridge.StopAsync(CancellationToken.None);

        var dispose = () => { bridge.Dispose(); bridge.Dispose(); };
        dispose.Should().NotThrow();

        (rig.Publisher.Disposals, rig.Subscriber.Disposals, rig.Bus.Live)
            .Should().Be((1, 1, 0), "the stop released everything once and neither disposal releases it again");
    }

    [Fact]
    public void Dispose_ShouldCallNoFactory_WhenTheBridgeNeverStarted()
    {
        var rig = new Rig();
        using var bridge = rig.Build(subscribe: true);

        var dispose = () => bridge.Dispose();
        dispose.Should().NotThrow();

        (rig.PublisherFactoryCalls, rig.SubscriberFactoryCalls, rig.Publisher.Disposals, rig.Subscriber.Disposals)
            .Should().Be((0, 0, 0, 0), "a bridge that never started opened nothing");
    }

    [Fact]
    public async Task Dispose_ShouldNotReturn_WhileAReleaseAnotherPathBeganIsInProgress()
    {
        var rig = new Rig(parkPublisherDispose: true);
        using var bridge = rig.Build(subscribe: false);

        await bridge.StartAsync(CancellationToken.None);
        await rig.Bus.Subscribed.WaitAsync(Fence); // the publisher was handed over before the bus subscription

        var stop = bridge.StopAsync(new CancellationToken(canceled: true));
        await rig.Publisher.DisposeEntered.WaitAsync(Fence); // the stop's release is parked in the publisher's dispose

        var dispose = Task.Run(bridge.Dispose);
        var returnedWhileParked = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(300))) == dispose; // fence-allow: SETTLE — the negative "has not returned while the dispose is parked" needs a bounded window; the gate below decides the positive

        rig.Publisher.OpenDisposeGate();
        var disposeEnded = await EndsWithinAsync(dispose, Fence);
        var stopEnded = await EndsWithinAsync(stop, Fence);

        (returnedWhileParked, disposeEnded, stopEnded, rig.Publisher.Disposals, rig.Publisher.DisposalsCompleted)
            .Should().Be(
                (false, true, true, 1, 1),
                "a disposal does not return while the release a stop began is still disposing the publisher");
    }

    // ---------------------------------------------------------------- a host

    [Fact]
    public async Task HostDispose_ShouldReleaseTheBridge_WhenALaterHostedServiceFailsToStart()
    {
        var rig = new Rig();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        // The shape AddPushNats registers: a singleton, and a hosted service resolved from it.
        builder.Services.AddSingleton(_ => rig.Build(subscribe: true));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NatsBridge>());
        builder.Services.AddHostedService(_ => new FailingStartService(rig.Subscriber.Subscribed));
        var host = builder.Build();

        var start = async () => await host.StartAsync();
        await start.Should().ThrowAsync<InvalidOperationException>();

        var dispose = async () => await ((IAsyncDisposable)host).DisposeAsync();
        await dispose.Should().NotThrowAsync();

        (rig.Publisher.Disposals, rig.Subscriber.Disposals, rig.Bus.Live)
            .Should().Be((1, 1, 0), "a host disposed after a failed start releases what the bridge opened");
    }

    [Fact]
    public async Task HostDispose_ShouldLeaveNoNatsConnectionOpen_WhenTheStartedHostIsDisposedWithoutAStop()
    {
        using var server = new FakeNatsServer();
        server.ReleaseInfo();
        var log = new LogCapture();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.Services.AddSingleton<IPushEventBus>(new CountingBus());
        builder.Services.AddPushNats(o =>
        {
            o.Url = $"nats://127.0.0.1:{server.Port}";
            o.SubjectPrefix = "asterisk.sdk";
            o.ConnectTimeoutSeconds = 5;
            o.NodeId = "release";
            o.Subscribe = new NatsSubscribeOptions { SubjectFilters = ["asterisk.sdk.release.>"] };
        });
        var host = builder.Build();

        await host.StartAsync();
        await log.Subscribing.WaitAsync(Fence); // the bridge holds its publisher and its subscriber

        var dispose = async () => await ((IAsyncDisposable)host).DisposeAsync();
        await dispose.Should().NotThrowAsync();
        var closed = await EndsWithinAsync(server.AllClosed, Fence);

        (server.Accepted, closed, server.Open)
            .Should().Be((1, true, 0), "a host disposed without a stop closes the bridge's one connection");
    }

    // ---------------------------------------------------------------- the default connection factory

    [Fact]
    public async Task StopAsync_ShouldCloseTheConnection_WhenTheStopCancelsADefaultConnectBeforeTheServerGreets()
    {
        using var server = new FakeNatsServer();
        var log = new LogCapture();
        using var bridge = BuildDefault(server, log, connectTimeoutSeconds: 5);

        await bridge.StartAsync(CancellationToken.None);
        await server.FirstAccept.WaitAsync(Fence); // the connect is in flight, waiting for INFO

        await bridge.StopAsync(CancellationToken.None); // cancels the connect
        server.ReleaseInfo(); // the server greets only now
        var closed = await EndsWithinAsync(server.AllClosed, Fence);

        (closed, server.Open, server.Pongs)
            .Should().Be((true, 0, 0), "a connect the stop gave up on is closed and never completes its handshake");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCloseTheConnection_WhenTheDefaultConnectTimesOutBeforeTheServerGreets()
    {
        using var server = new FakeNatsServer();
        var log = new LogCapture();
        using var bridge = BuildDefault(server, log, connectTimeoutSeconds: 1);

        await bridge.StartAsync(CancellationToken.None);
        await server.FirstAccept.WaitAsync(Fence);
        var executeEnded = await EndsWithinAsync(bridge.ExecuteTask!, Fence); // the connect timeout ends the bridge's work

        server.ReleaseInfo(); // the server greets only after the timeout
        var closed = await EndsWithinAsync(server.AllClosed, Fence);
        await bridge.StopAsync(CancellationToken.None);

        (executeEnded, closed, server.Open, server.Pongs)
            .Should().Be((true, true, 0, 0), "a connect that timed out is closed and never completes its handshake");
    }

    [Fact]
    public async Task StopAsync_ShouldCloseTheConnection_WhenTheDefaultFactoryConnected()
    {
        using var server = new FakeNatsServer();
        server.ReleaseInfo();
        var log = new LogCapture();
        using var bridge = BuildDefault(server, log, connectTimeoutSeconds: 5);

        await bridge.StartAsync(CancellationToken.None);
        await log.Connected.WaitAsync(Fence); // the factory returned a connected publisher

        await bridge.StopAsync(CancellationToken.None);
        var closed = await EndsWithinAsync(server.AllClosed, Fence);

        (server.Accepted, closed, server.Open)
            .Should().Be((1, true, 0), "a connected bridge's stop closes its connection");
    }

    // ---------------------------------------------------------------- harness

    /// <summary>Awaits <paramref name="task"/> for at most <paramref name="timeout"/>; reports whether it ended.</summary>
    private static async Task<bool> EndsWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // ended, cancelled: an ending all the same
        }
        catch (InvalidOperationException)
        {
            // ended, faulted: the assertion reads the counts, not the fault
        }
        return true;
    }

    private static NatsBridge BuildDefault(FakeNatsServer server, LogCapture log, int connectTimeoutSeconds)
    {
        var options = Options.Create(new NatsBridgeOptions
        {
            Url = $"nats://127.0.0.1:{server.Port}",
            SubjectPrefix = "asterisk.sdk",
            ConnectTimeoutSeconds = connectTimeoutSeconds,
        });
        return new NatsBridge(
            new CountingBus(), options, new DefaultNatsPayloadSerializer(options), new DefaultNatsPayloadDeserializer(), log);
    }

    /// <summary>One bridge's fakes, counters and gates.</summary>
    private sealed class Rig(bool parkPublisher = false, bool parkSubscriber = false, bool parkPublisherDispose = false)
    {
        private readonly TaskCompletionSource _publisherEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _publisherGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _subscriberEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _subscriberGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _publisherFactoryCalls;
        private int _subscriberFactoryCalls;

        public CountingBus Bus { get; } = new();
        public CountingPublisher Publisher { get; } = new(parkPublisherDispose);
        public CountingSubscriber Subscriber { get; } = new();
        public LogCapture Log { get; } = new();
        public Task PublisherEntered => _publisherEntered.Task;
        public Task SubscriberEntered => _subscriberEntered.Task;
        public int PublisherFactoryCalls => Volatile.Read(ref _publisherFactoryCalls);
        public int SubscriberFactoryCalls => Volatile.Read(ref _subscriberFactoryCalls);

        public void OpenPublisherGate() => _publisherGate.TrySetResult();
        public void OpenSubscriberGate() => _subscriberGate.TrySetResult();

        public NatsBridge Build(bool subscribe)
        {
            var options = Options.Create(new NatsBridgeOptions
            {
                Url = "nats://127.0.0.1:4222",
                SubjectPrefix = "asterisk.sdk",
                Subscribe = subscribe ? new NatsSubscribeOptions { SubjectFilters = ["asterisk.sdk.release.>"] } : null,
            });

            // Both factories ignore the stopping token while parked, like a connect that does not observe it.
            return new NatsBridge(
                Bus, options, new DefaultNatsPayloadSerializer(options), new DefaultNatsPayloadDeserializer(),
                new NatsMetrics(), new Logger<NatsBridge>(Log),
                publisherFactory: async (_, _) =>
                {
                    Interlocked.Increment(ref _publisherFactoryCalls);
                    if (parkPublisher)
                    {
                        _publisherEntered.TrySetResult();
                        await _publisherGate.Task;
                    }
                    return Publisher;
                },
                subscriberFactory: async (_, _) =>
                {
                    Interlocked.Increment(ref _subscriberFactoryCalls);
                    if (parkSubscriber)
                    {
                        _subscriberEntered.TrySetResult();
                        await _subscriberGate.Task;
                    }
                    return Subscriber;
                });
        }
    }

    private sealed class CountingPublisher(bool parkDispose) : INatsPublisher
    {
        private readonly TaskCompletionSource _disposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposals;
        private int _disposalsCompleted;

        public int Disposals => Volatile.Read(ref _disposals);
        public int DisposalsCompleted => Volatile.Read(ref _disposalsCompleted);
        public Task DisposeEntered => _disposeEntered.Task;

        public void OpenDisposeGate() => _disposeGate.TrySetResult();

        public ValueTask PublishAsync(string subject, byte[] payload, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            if (parkDispose)
            {
                _disposeEntered.TrySetResult();
                await _disposeGate.Task;
            }
            Interlocked.Increment(ref _disposalsCompleted);
        }
    }

    private sealed class CountingSubscriber : INatsSubscriber
    {
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposals;
        private int _subscriptions;

        public int Disposals => Volatile.Read(ref _disposals);
        public int Subscriptions => Volatile.Read(ref _subscriptions);
        public Task Subscribed => _subscribed.Task;

        public async IAsyncEnumerable<NatsSubscriberMessage> SubscribeAsync(
            string subject,
            string? queueGroup,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _subscriptions);
            _subscribed.TrySetResult();

            // Parks until the bridge's stopping token ends the loop.
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => stopped.TrySetResult()))
            {
                await stopped.Task;
            }
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingBus : IPushEventBus, IObservable<PushEvent>
    {
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _live;

        public int Live => Volatile.Read(ref _live);
        public Task Subscribed => _subscribed.Task;

        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default) where TEvent : PushEvent =>
            ValueTask.CompletedTask;

        public IObservable<PushEvent> AsObservable() => this;

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => throw new NotSupportedException();

        public IDisposable Subscribe(IObserver<PushEvent> observer)
        {
            Interlocked.Increment(ref _live);
            _subscribed.TrySetResult();
            return new Unsubscriber(this);
        }

        private sealed class Unsubscriber(CountingBus bus) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref bus._live);
            }
        }
    }

    /// <summary>A hosted service whose start throws once the bridge registered before it has subscribed.</summary>
    private sealed class FailingStartService(Task bridgeSubscribed) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await bridgeSubscribed.WaitAsync(Fence, cancellationToken);
            throw new InvalidOperationException("a hosted service registered after the bridge fails to start");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Records the bridge's own log lines (the NATS client's categories are disabled).</summary>
    private sealed class LogCapture : ILoggerFactory, ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _subscribing = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Connected => _connected.Task;
        public Task Subscribing => _subscribing.Task;

        public int Count(string prefix) => _messages.Count(m => m.StartsWith(prefix, StringComparison.Ordinal));

        public void AddProvider(ILoggerProvider provider)
        {
            // a fixed sink: nothing else receives these lines
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
            // nothing to release: the queue is read after the bridge ended
        }

        private void Record(string message)
        {
            _messages.Enqueue(message);
            if (message.StartsWith("NATS bridge connected", StringComparison.Ordinal)) _connected.TrySetResult();
            if (message.StartsWith("NATS bridge subscribing", StringComparison.Ordinal)) _subscribing.TrySetResult();
        }

        private sealed class CapturingLogger(LogCapture sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category.EndsWith(nameof(NatsBridge), StringComparison.Ordinal);

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Record(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// A NATS-shaped loopback peer: accepts, withholds <c>INFO</c> until released, then answers every
    /// <c>PING</c> with <c>PONG</c>; counts accepted and still-open sockets and the PONGs it sent.
    /// </summary>
    private sealed class FakeNatsServer : IDisposable
    {
        private const string Info =
            "INFO {\"server_id\":\"release\",\"server_name\":\"release\",\"version\":\"2.10.0\",\"proto\":1,"
            + "\"go\":\"go1.22\",\"host\":\"127.0.0.1\",\"port\":4222,\"headers\":true,\"max_payload\":1048576}\r\n";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBag<Socket> _sockets = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstAccept = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _accepted;
        private int _open;
        private int _pongs;

        public FakeNatsServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public int Accepted => Volatile.Read(ref _accepted);
        public int Open => Volatile.Read(ref _open);
        public int Pongs => Volatile.Read(ref _pongs);
        public Task FirstAccept => _firstAccept.Task;

        /// <summary>Completes once at least one socket was accepted and every accepted socket closed.</summary>
        public Task AllClosed => _allClosed.Task;

        public void ReleaseInfo() => _release.TrySetResult();

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var socket in _sockets) socket.Dispose();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var socket = await _listener.AcceptSocketAsync(_cts.Token);
                    _sockets.Add(socket);
                    Interlocked.Increment(ref _accepted);
                    Interlocked.Increment(ref _open);
                    _firstAccept.TrySetResult();
                    _ = ServeAsync(socket);
                }
            }
            catch (OperationCanceledException)
            {
                // the test disposed the server
            }
            catch (SocketException)
            {
                // the listener was stopped
            }
        }

        private async Task ServeAsync(Socket socket)
        {
            using (socket)
            {
                try
                {
                    await _release.Task.WaitAsync(_cts.Token);
                    await socket.SendAsync(Encoding.ASCII.GetBytes(Info), SocketFlags.None, _cts.Token);
                    var buffer = new byte[8192];
                    var pending = new StringBuilder();
                    while (true)
                    {
                        var read = await socket.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                        if (read == 0) break;
                        pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        var text = pending.ToString();
                        int end;
                        while ((end = text.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
                        {
                            var line = text[..end];
                            text = text[(end + 2)..];
                            if (line.StartsWith("PING", StringComparison.Ordinal))
                            {
                                await socket.SendAsync("PONG\r\n"u8.ToArray(), SocketFlags.None, _cts.Token);
                                Interlocked.Increment(ref _pongs);
                            }
                        }
                        pending.Clear().Append(text);
                    }
                }
                catch (OperationCanceledException)
                {
                    // the test disposed the server
                }
                catch (SocketException)
                {
                    // the client reset the connection: closed all the same
                }
                catch (ObjectDisposedException)
                {
                    // the test disposed the server
                }
                finally
                {
                    if (Interlocked.Decrement(ref _open) == 0) _allClosed.TrySetResult();
                }
            }
        }
    }
}
