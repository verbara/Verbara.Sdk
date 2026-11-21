using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using Verbara.Sdk.Ari.Outbound;
using Verbara.Sdk.Ari.Tests.TestSupport;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Outbound;

/// <summary>
/// What an observer of an ARI outbound listener's announcements, or of an outbound connection's events, may and may not
/// change when it throws: every other observer is still told, the connection it threw on stays open and served, the
/// listener's disposal finishes, and each failure is counted once and logged once at Warning.
/// </summary>
/// <remarks>
/// <para>Runs in <see cref="AriOutboundSubscriberFaultsGroup"/>: the counter read here is process-wide. It is matched by
/// name, so this file also compiles on a tree that does not define it, where a sum stays 0.</para>
/// <para>Every wait ends on a signal or on the event a test asserts, bounded by <see cref="WaitLimit"/>. An observer
/// that subscribes to a connection's events does so while the connection is announced, before the peer sends anything,
/// so no event can fall before a subscription.</para>
/// </remarks>
[Collection(AriOutboundSubscriberFaultsGroup.Name)]
public sealed class AriOutboundSubscriberIsolationTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private const string FaultsInstrument = "ari.outbound.subscriber_faults";

    // ------------------------------------------------------------------ OnConnectionAccepted

    [Fact]
    public async Task OnConnectionAccepted_ShouldTellTheLaterObserverAndKeepTheConnectionServed_WhenAnEarlierObserverThrows()
    {
        // Arrange
        using var faults = new InstrumentSum(FaultsInstrument);
        var logger = new CapturingLogger<AriOutboundListener>();
        await using var listener = CreateListener(logger);
        using var first = listener.OnConnectionAccepted.Subscribe(
            _ => throw new InvalidOperationException("the first observer failed"));
        var told = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<AriEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? events = null;
        using var second = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            events = connection.Events.Subscribe(evt => received.TrySetResult(evt));
            told.TrySetResult(connection);
        });
        await listener.StartAsync();

        try
        {
            // Act
            using var peer = await ConnectAsync(listener.BoundPort, "first-app");
            var connection = await WithinLimitAsync(told.Task, "the later observer is told about the connection");
            await SendEventAsync(peer, "first-app");
            var delivered = await WithinLimitAsync(received.Task, "the connection it was told about is served");

            // Assert
            using (new AssertionScope())
            {
                delivered!.Type.Should().Be("StasisStart");
                connection!.IsConnected.Should().BeTrue("an observer that throws does not close the connection");
                listener.ActiveConnectionCount.Should().Be(1, "the connection stays tracked");
                listener.GetByApplication("first-app").Should().ContainSingle(c => ReferenceEquals(c, connection));
                faults.Total.Should().Be(1, "one observer threw once");
                faults.For("connection_accepted").Should().Be(1, "the failure is the announcement's");
                logger.Entries.Should().ContainSingle(
                    e => e.Level == LogLevel.Warning && e.EventName == "AcceptedObserverFault",
                    "the failure is logged once at Warning; the listener logged [{0}]",
                    Describe(logger));
                logger.Entries.Single(e => e.EventName == "AcceptedObserverFault").Exception
                    .Should().BeOfType<InvalidOperationException>("the observer's exception is attached");
                logger.Entries.Should().NotContain(
                    e => e.EventName == "ConnectionError",
                    "the connection did not fail: its observer did");
            }
        }
        finally
        {
            events?.Dispose();
        }
    }

    [Fact]
    public async Task OnConnectionAccepted_ShouldKeepTellingTheObserverThatThrew_WhenItThrewOnAnEarlierConnection()
    {
        // Arrange
        using var faults = new InstrumentSum(FaultsInstrument);
        var logger = new CapturingLogger<AriOutboundListener>();
        await using var listener = CreateListener(logger);
        var calls = 0;
        var secondTold = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("the observer failed on the first connection");
            secondTold.TrySetResult(connection);
        });
        await listener.StartAsync();

        // Act
        using var firstPeer = await ConnectAsync(listener.BoundPort, "first-app");
        await WithinLimitAsync(
            logger.WhenLogged(e => e.EventName is "AcceptedObserverFault" or "ConnectionError"),
            "the first announcement's failure is reported");
        using var secondPeer = await ConnectAsync(listener.BoundPort, "second-app");
        var second = await WithinLimitAsync(secondTold.Task, "a direct subscriber that threw stays subscribed");

        // Assert
        using (new AssertionScope())
        {
            second!.ApplicationName.Should().Be("second-app");
            listener.ActiveConnectionCount.Should().Be(2, "neither connection was lost to the observer");
            faults.Total.Should().Be(1);
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteTheLaterObserverAndFinish_WhenAnObserverThrowsOnTheAnnouncementsEnd()
    {
        // Arrange
        using var faults = new InstrumentSum(FaultsInstrument);
        var logger = new CapturingLogger<AriOutboundListener>();
        var listener = CreateListener(logger);
        using var first = listener.OnConnectionAccepted.Subscribe(
            static _ => { },
            static () => throw new InvalidOperationException("the first observer failed on the completion"));
        var completed = 0;
        using var second = listener.OnConnectionAccepted.Subscribe(static _ => { }, () => Interlocked.Increment(ref completed));
        await listener.StartAsync();

        // Act
        var thrown = await Record.ExceptionAsync(async () => await listener.DisposeAsync().AsTask().WaitAsync(WaitLimit));

        // Assert
        using (new AssertionScope())
        {
            thrown.Should().BeNull("an observer that throws on the completion does not keep the disposal from finishing");
            Volatile.Read(ref completed).Should().Be(1, "the later observer still receives the completion");
            listener.IsRunning.Should().BeFalse();
            faults.For("connection_accepted").Should().Be(1);
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning && e.EventName == "AcceptedObserverFault",
                "the listener logged [{0}]",
                Describe(logger));
        }
    }

    [Fact]
    public async Task OnConnectionAccepted_ShouldKeepTheConnectionAndCountTheFault_WhenTheLoggerThrowsOnTheReport()
    {
        // Arrange — a logger whose provider fails on the Warning the report writes
        using var faults = new InstrumentSum(FaultsInstrument);
        await using var listener = CreateListener(new ThrowingOnWarningLogger());
        using var first = listener.OnConnectionAccepted.Subscribe(
            _ => throw new InvalidOperationException("the first observer failed"));
        var told = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var second = listener.OnConnectionAccepted.Subscribe(connection => told.TrySetResult(connection));
        await listener.StartAsync();

        // Act
        using var peer = await ConnectAsync(listener.BoundPort, "first-app");
        var connection = await WithinLimitAsync(told.Task, "a logger that throws on the report stops nothing");

        // Assert
        using (new AssertionScope())
        {
            connection!.IsConnected.Should().BeTrue();
            listener.ActiveConnectionCount.Should().Be(1);
            faults.Total.Should().Be(1, "the fault is counted before the line that failed");
        }
    }

    // ------------------------------------------------------------------ AriOutboundConnection.Events

    [Fact]
    public async Task Events_ShouldDeliverEveryEventToEveryDirectObserverAndKeepTheConnection_WhenAnObserverThrowsOnEveryEvent()
    {
        // Arrange
        using var faults = new InstrumentSum(FaultsInstrument);
        var logger = new CapturingLogger<AriOutboundListener>();
        await using var listener = CreateListener(logger);
        var thrower = 0;
        var received = new ConcurrentQueue<AriEvent>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new ConcurrentBag<IDisposable>();
        var told = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var announced = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            subscriptions.Add(connection.Events.Subscribe(_ =>
            {
                Interlocked.Increment(ref thrower);
                throw new InvalidOperationException("the first events observer failed");
            }));
            subscriptions.Add(connection.Events.Subscribe(evt =>
            {
                received.Enqueue(evt);
                if (received.Count == 2)
                    both.TrySetResult();
            }));
            told.TrySetResult(connection);
        });
        await listener.StartAsync();

        try
        {
            // Act
            using var peer = await ConnectAsync(listener.BoundPort, "first-app");
            var connection = await WithinLimitAsync(told.Task, "the connection is announced");
            await SendEventAsync(peer, "first-app");
            await SendEventAsync(peer, "first-app");
            await WithinLimitAsync(both.Task, "the later observer receives both events");

            // Assert
            using (new AssertionScope())
            {
                received.Should().HaveCount(2);
                Volatile.Read(ref thrower).Should().Be(2, "a direct subscriber that threw stays subscribed");
                connection!.IsConnected.Should().BeTrue("an observer that throws does not close the connection");
                listener.ActiveConnectionCount.Should().Be(1);
                faults.Total.Should().Be(2, "one fault per event the observer threw on");
                faults.For("events").Should().Be(2);
                logger.Entries.Where(e => e.Level == LogLevel.Warning && e.EventName == "EventsObserverFault")
                    .Should().HaveCount(2, "each failure is logged once; the listener logged [{0}]", Describe(logger));
                logger.Entries.Should().NotContain(e => e.EventName == "ConnectionError");
            }
        }
        finally
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }

    [Fact]
    public async Task Events_ShouldCompleteTheLaterObserver_WhenAnObserverThrowsOnTheCompletion()
    {
        // Arrange
        using var faults = new InstrumentSum(FaultsInstrument);
        var logger = new CapturingLogger<AriOutboundListener>();
        await using var listener = CreateListener(logger);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new ConcurrentBag<IDisposable>();
        using var announced = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            subscriptions.Add(connection.Events.Subscribe(
                static _ => { },
                static () => throw new InvalidOperationException("the first events observer failed on the completion")));
            subscriptions.Add(connection.Events.Subscribe(static _ => { }, () => completed.TrySetResult()));
        });
        await listener.StartAsync();

        try
        {
            // Act — the peer closes, the read pump ends and the connection completes its events
            using var peer = await ConnectAsync(listener.BoundPort, "first-app");
            await WithinLimitAsync(WhenAsync(() => listener.ActiveConnectionCount == 1), "the connection is served");
            await peer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(WaitLimit);
            var ended = await Task.WhenAny(completed.Task, Task.Delay(WaitLimit)) == completed.Task;
            await WithinLimitAsync(WhenAsync(() => listener.ActiveConnectionCount == 0), "the connection is released");

            // Assert
            using (new AssertionScope())
            {
                ended.Should().BeTrue("the later observer receives the completion the first one threw on");
                faults.For("events").Should().Be(1);
                logger.Entries.Should().ContainSingle(
                    e => e.Level == LogLevel.Warning && e.EventName == "EventsObserverFault",
                    "the listener logged [{0}]",
                    Describe(logger));
            }
        }
        finally
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }

    [Fact]
    public async Task Events_ShouldEndOnlyTheComposedSubscription_WhenAHandlerComposedThroughAnOperatorThrows()
    {
        // Arrange
        using var faults = new InstrumentSum(FaultsInstrument);
        await using var listener = CreateListener(new CapturingLogger<AriOutboundListener>());
        var composedCalls = 0;
        var received = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new ConcurrentBag<IDisposable>();
        var told = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var announced = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            subscriptions.Add(connection.Events.Where(static _ => true).Subscribe(_ =>
            {
                Interlocked.Increment(ref composedCalls);
                throw new InvalidOperationException("the composed handler failed");
            }));
            subscriptions.Add(connection.Events.Subscribe(_ =>
            {
                if (Interlocked.Increment(ref received) == 3)
                    all.TrySetResult();
            }));
            told.TrySetResult(connection);
        });
        await listener.StartAsync();

        try
        {
            // Act
            using var peer = await ConnectAsync(listener.BoundPort, "first-app");
            var connection = await WithinLimitAsync(told.Task, "the connection is announced");
            for (var i = 0; i < 3; i++)
                await SendEventAsync(peer, "first-app");
            await WithinLimitAsync(all.Task, "the direct observer receives all three events");

            // Assert
            using (new AssertionScope())
            {
                Volatile.Read(ref composedCalls).Should().Be(1, "the operator ends its subscription on the first failure");
                faults.For("events").Should().Be(1, "the composed subscription's failure is counted once");
                connection!.IsConnected.Should().BeTrue();
            }
        }
        finally
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }

    [Fact]
    public async Task Events_ShouldCountTheFaultAndKeepDelivering_WhenTheLoggerThrowsOnTheReport()
    {
        // Arrange — the connection reports with the listener's logger, whose provider fails on the Warning
        using var faults = new InstrumentSum(FaultsInstrument);
        await using var listener = CreateListener(new ThrowingOnWarningLogger());
        var received = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new ConcurrentBag<IDisposable>();
        var told = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var announced = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            subscriptions.Add(connection.Events.Subscribe(
                static _ => throw new InvalidOperationException("the first events observer failed")));
            subscriptions.Add(connection.Events.Subscribe(_ =>
            {
                if (Interlocked.Increment(ref received) == 2)
                    both.TrySetResult();
            }));
            told.TrySetResult(connection);
        });
        await listener.StartAsync();

        try
        {
            // Act
            using var peer = await ConnectAsync(listener.BoundPort, "first-app");
            var connection = await WithinLimitAsync(told.Task, "the connection is announced");
            await SendEventAsync(peer, "first-app");
            await SendEventAsync(peer, "first-app");
            await WithinLimitAsync(both.Task, "a logger that throws on the report stops no delivery");

            // Assert
            using (new AssertionScope())
            {
                faults.For("events").Should().Be(2, "each fault is counted before the line that failed");
                connection!.IsConnected.Should().BeTrue();
            }
        }
        finally
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static AriOutboundListener CreateListener(ILogger<AriOutboundListener> logger) =>
        new(
            Options.Create(new AriOutboundListenerOptions
            {
                ListenAddress = "127.0.0.1",
                Port = 0,
                Path = "/ari/events",
                ConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            }),
            logger,
            TimeProvider.System);

    private static async Task<ClientWebSocket> ConnectAsync(int port, string app)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ari/events?app={app}"), CancellationToken.None)
            .WaitAsync(WaitLimit);
        return client;
    }

    private static Task SendEventAsync(ClientWebSocket peer, string app)
    {
        var json = $$$"""{"type":"StasisStart","application":"{{{app}}}","timestamp":"2026-04-19T10:00:00Z","args":[],"channel":{"id":"ch-1","name":"PJSIP/test","state":"Up"}}""";
        return peer.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
            .WaitAsync(WaitLimit);
    }

    /// <summary>The task's result, or null after <see cref="WaitLimit"/>; reaching the limit fails the test.</summary>
    private static async Task<T?> WithinLimitAsync<T>(Task<T> task, string because)
    {
        var finished = await Task.WhenAny(task, Task.Delay(WaitLimit));
        (finished == task).Should().BeTrue(because);
        return finished == task ? await task : default;
    }

    private static async Task WithinLimitAsync(Task task, string because)
    {
        var finished = await Task.WhenAny(task, Task.Delay(WaitLimit));
        (finished == task).Should().BeTrue(because);
    }

    private static async Task WhenAsync(Func<bool> predicate)
    {
        while (!predicate())
            await Task.Delay(10);
    }

    private static string Describe(CapturingLogger<AriOutboundListener> logger) =>
        string.Join(", ", logger.Entries.Select(e => $"{e.Level}:{e.EventName}({e.Exception?.GetType().Name})"));

    /// <summary>Sums what is added to one counter of the ARI meter, matched by name, in total and per surface tag.</summary>
    private sealed class InstrumentSum : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _bySurface = new(StringComparer.Ordinal);
        private long _total;

        public InstrumentSum(string name)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ari" && instrument.Name == name)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                Interlocked.Add(ref _total, value);
                foreach (var tag in tags)
                {
                    if (tag.Key == "surface" && tag.Value is string surface)
                        _bySurface.AddOrUpdate(surface, value, (_, sum) => sum + value);
                }
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public long For(string surface) => _bySurface.TryGetValue(surface, out var sum) ? sum : 0;

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A logger whose provider fails on a Warning, as a broken sink would.</summary>
    private sealed class ThrowingOnWarningLogger : ILogger<AriOutboundListener>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                throw new InvalidOperationException("the log provider failed");
        }
    }
}
