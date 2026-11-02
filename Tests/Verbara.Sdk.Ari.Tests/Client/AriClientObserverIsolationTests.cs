using System.Diagnostics.Metrics;
using System.Reactive.Linq;
using System.Reflection;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Internal;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// What an ARI observer that throws may and may not change: the other observers get that event and every later one,
/// on that connection and after a reconnect; the thrower stays subscribed; the event pump's consumer goes on; and a
/// disposal completes and releases what the client holds. Each failure is reported once.
/// </summary>
/// <remarks>
/// <para>Runs in <see cref="AriEventsDroppedGroup"/>: the instruments read here are process-wide, and any other test
/// whose client handles an event moves them. They are matched by name, so this file also compiles on a tree that does
/// not define them, where a sum stays 0.</para>
/// <para>Every wait ends on a signal or on the event a test asserts, bounded by <see cref="WaitLimit"/>. A marker event
/// sent last, which reaches an observer that throws on nothing, ends a wait where a later delivery would have happened.
/// </para>
/// </remarks>
[Collection(AriEventsDroppedGroup.Name)]
public sealed class AriClientObserverIsolationTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static readonly FieldInfo PumpField =
        typeof(AriClient).GetField("_pump", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PumpConsumerField =
        typeof(AriEventPump).GetField("_consumerTask", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task Subscribe_ShouldDeliverEveryEventInOrderToTheSecondObserver_WhenTheFirstThrowsOnEveryEvent()
    {
        const int events = 5;
        await using var farEnd = new AriFarEnd();
        var sut = Client(farEnd);
        var first = new Recorder(_ => true);
        var second = new Recorder();
        using var firstSubscription = sut.Subscribe(first);
        using var secondSubscription = sut.Subscribe(second);
        try
        {
            await sut.ConnectAsync();
            for (var i = 0; i < events; i++)
                await farEnd.SendAsync($"Probe-{i}");

            var all = await second.WhenReceived(events);

            using (new AssertionScope())
            {
                all.Should().BeTrue($"the first observer's failure is its own. Measured: {Describe(sut, second)}");
                second.Types.Should().Equal(Enumerable.Range(0, events).Select(i => $"Probe-{i}"), "in the order sent");
                first.Calls.Should().Be(events, "the thrower stays subscribed and is called for every event");
            }
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    [Fact]
    public async Task Subscribe_ShouldDeliverLaterEventsToEveryObserver_WhenAnObserverThrewOnce()
    {
        await using var farEnd = new AriFarEnd();
        var sut = Client(farEnd);
        var thrower = new Recorder(evt => evt.Type == "Probe-0");
        var other = new Recorder();
        using var throwerSubscription = sut.Subscribe(thrower);
        using var otherSubscription = sut.Subscribe(other);
        try
        {
            await sut.ConnectAsync();
            await farEnd.SendAsync("Probe-0");
            await thrower.WhenCalled(1);
            await farEnd.SendAsync("Probe-1");
            await farEnd.SendAsync("Probe-2");

            var all = await other.WhenReceived(3);

            using (new AssertionScope())
            {
                ConsumerStatus(sut).Should().Be(TaskStatus.WaitingForActivation,
                    $"the pump's consumer is still waiting for events. Measured: {Describe(sut, other)}");
                all.Should().BeTrue($"nothing ended delivery. Measured: {Describe(sut, other)}");
                other.Types.Should().Equal("Probe-0", "Probe-1", "Probe-2");
                thrower.Types.Should().Equal("Probe-1", "Probe-2");
                thrower.Calls.Should().Be(3, "an observer that threw stays subscribed");
            }
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    [Fact]
    public async Task Subscribe_ShouldDeliverToEveryObserverAfterAReconnect_WhenAnObserverThrewOnTheFirstConnection()
    {
        await using var farEnd = new AriFarEnd();
        var sut = Client(farEnd, autoReconnect: true);
        var thrower = new Recorder(evt => evt.Type == "Probe-0");
        var other = new Recorder();
        using var throwerSubscription = sut.Subscribe(thrower);
        using var otherSubscription = sut.Subscribe(other);
        try
        {
            await sut.ConnectAsync();
            await farEnd.SendAsync("Probe-0");
            await thrower.WhenCalled(1);

            farEnd.Drop();
            await farEnd.WhenUpgraded(2).WaitAsync(WaitLimit);
            await farEnd.SendAsync("After-reconnect");

            var both = await other.WhenSaw("After-reconnect") && await thrower.WhenSaw("After-reconnect");

            both.Should().BeTrue($"the reconnect keeps a pump that still delivers. Measured: {Describe(sut, other)}");
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReleaseTheClientAndCompleteTheOtherObserver_WhenAnObserverThrowsFromOnCompleted()
    {
        await using var farEnd = new AriFarEnd();
        var sut = Client(farEnd);
        var thrower = new Recorder(throwOnCompleted: true);
        var other = new Recorder();
        sut.Subscribe(thrower);
        sut.Subscribe(other);
        await sut.ConnectAsync();

        var dispose = async () => await sut.DisposeAsync();
        var escaped = await Record(dispose);
        var restCall = async () => await sut.Asterisk.PingAsync();
        var afterDisposal = await Record(restCall);

        using (new AssertionScope())
        {
            escaped.Should().BeNull("nothing an observer throws at the end of the stream reaches the caller");
            other.Completed.Should().BeTrue("the other observer still hears the end of the stream");
            afterDisposal.Should().BeOfType<ObjectDisposedException>(
                $"the HTTP client is released, as on any disposed client. Requests that reached the far end: {farEnd.Requests.Count}");
            sut.State.Should().Be(AriConnectionState.Disconnected);
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReleaseTheClient_WhenTheOnlyObserverThrowsFromOnCompletedOnAClientNeverConnected()
    {
        var sut = new AriClient(Options("http://127.0.0.1:1"), new CapturingLogger<AriClient>());
        var thrower = new Recorder(throwOnCompleted: true);
        sut.Subscribe(thrower);

        var dispose = async () => await sut.DisposeAsync();
        var escaped = await Record(dispose);
        var restCall = async () => await sut.Asterisk.PingAsync();
        var afterDisposal = await Record(restCall);

        using (new AssertionScope())
        {
            escaped.Should().BeNull("a disposal completes whatever an observer does");
            afterDisposal.Should().BeOfType<ObjectDisposedException>("the HTTP client is released on this path too");
        }
    }

    [Fact]
    public async Task Subscribe_ShouldReportOneWarningAndOneFault_WhenAnObserverThrowsOnOneEvent()
    {
        var failure = new InvalidOperationException("observer failed");
        using var faults = new InstrumentSum("ari.events.subscriber_faults");
        using var dropped = new InstrumentSum("ari.events.dropped");
        await using var farEnd = new AriFarEnd();
        var logger = new CapturingLogger<AriClient>();
        var sut = new AriClient(Options(farEnd.BaseUrl), logger);
        var thrower = new Recorder(evt => evt.Type == "Probe-0", failure);
        var other = new Recorder();
        using var throwerSubscription = sut.Subscribe(thrower);
        using var otherSubscription = sut.Subscribe(other);
        try
        {
            await sut.ConnectAsync();
            await farEnd.SendAsync("Probe-0");
            await other.WhenReceived(1);

            var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();

            using (new AssertionScope())
            {
                warnings.Should().ContainSingle("one failure is one Warning");
                warnings.FirstOrDefault()?.Exception.Should().BeSameAs(failure);
                warnings.FirstOrDefault()?.Value("EventType").Should().Be("Probe-0", "the Warning names the event's type");
                faults.Total.Should().Be(1);
                dropped.Total.Should().Be(0, "a delivered event is not a dropped one");
            }
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReportOneWarningAndOneFaultNamingTheCompletion_WhenAnObserverThrowsFromOnCompleted()
    {
        using var faults = new InstrumentSum("ari.events.subscriber_faults");
        var logger = new CapturingLogger<AriClient>();
        var sut = new AriClient(Options("http://127.0.0.1:1"), logger);
        sut.Subscribe(new Recorder(throwOnCompleted: true));

        await QuietDisposeAsync(sut);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        using (new AssertionScope())
        {
            warnings.Should().ContainSingle("one failure is one Warning");
            warnings.FirstOrDefault()?.Value("EventType").Should().Be("completion");
            faults.Total.Should().Be(1);
        }
    }

    [Fact]
    public async Task Subscribe_ShouldStillDeliverAndCount_WhenTheLoggerThrowsOnTheReport()
    {
        using var faults = new InstrumentSum("ari.events.subscriber_faults");
        await using var farEnd = new AriFarEnd();
        var sut = new AriClient(Options(farEnd.BaseUrl), new ThrowingOnWarningLogger());
        var thrower = new Recorder(_ => true);
        var other = new Recorder();
        using var throwerSubscription = sut.Subscribe(thrower);
        using var otherSubscription = sut.Subscribe(other);
        try
        {
            await sut.ConnectAsync();
            await farEnd.SendAsync("Probe-0");
            await farEnd.SendAsync("Probe-1");

            var all = await other.WhenReceived(2);

            using (new AssertionScope())
            {
                all.Should().BeTrue($"what the report throws goes no further. Measured: {Describe(sut, other)}");
                faults.Total.Should().Be(2, "the fault is counted before it is reported");
            }
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    [Fact]
    public async Task Subscribe_ShouldEndOnlyTheComposedSubscription_WhenAHandlerComposedThroughAnOperatorThrows()
    {
        using var faults = new InstrumentSum("ari.events.subscriber_faults");
        await using var farEnd = new AriFarEnd();
        var logger = new CapturingLogger<AriClient>();
        var sut = new AriClient(Options(farEnd.BaseUrl), logger);
        var handled = 0;
        using var composed = Observable.Create<AriEvent>(sut.Subscribe)
            .Where(evt => evt.Type is not null)
            .Subscribe(_ =>
            {
                Interlocked.Increment(ref handled);
                throw new InvalidOperationException("handler failed");
            });
        var direct = new Recorder();
        using var directSubscription = sut.Subscribe(direct);
        try
        {
            await sut.ConnectAsync();
            await farEnd.SendAsync("Probe-0");
            await farEnd.SendAsync("Probe-1");
            await farEnd.SendAsync("Probe-2");

            var all = await direct.WhenReceived(3);

            using (new AssertionScope())
            {
                all.Should().BeTrue($"the direct observer keeps receiving. Measured: {Describe(sut, direct)}");
                Volatile.Read(ref handled).Should().Be(1, "the operator ended its subscription on the first failure");
                faults.Total.Should().Be(1, "that one failure is reported");
                logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle();
            }
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    [Fact]
    public async Task Subscribe_ShouldCountEveryEventAsDispatched_WhenAnObserverThrowsOnEveryEvent()
    {
        const int events = 3;
        using var dispatched = new InstrumentSum("ari.events.dispatched");
        await using var farEnd = new AriFarEnd();
        var sut = Client(farEnd);
        var thrower = new Recorder(_ => true);
        using var subscription = sut.Subscribe(thrower);
        var marker = new Recorder();
        using var markerSubscription = sut.Subscribe(marker);
        try
        {
            await sut.ConnectAsync();
            for (var i = 0; i < events; i++)
                await farEnd.SendAsync($"Probe-{i}");
            await farEnd.SendAsync("Marker");
            await marker.WhenSaw("Marker");
            await farEnd.SendAsync("Last");
            await marker.WhenSaw("Last");

            dispatched.Total.Should().BeGreaterThanOrEqualTo(events + 1,
                "the exception no longer leaves the dispatch, so the counter records the events an observer threw on");
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    [Fact]
    public void Subscribe_ShouldThrowArgumentNullException_WhenTheObserverIsNull()
    {
        var sut = new AriClient(Options("http://127.0.0.1:1"), new CapturingLogger<AriClient>());

        var subscribe = () => sut.Subscribe(null!);

        subscribe.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Subscribe_ShouldStopDelivering_WhenTheSubscriptionIsDisposed()
    {
        await using var farEnd = new AriFarEnd();
        var sut = Client(farEnd);
        var gone = new Recorder();
        var stays = new Recorder();
        using var subscription = sut.Subscribe(gone);
        using var staysSubscription = sut.Subscribe(stays);
        try
        {
            await sut.ConnectAsync();
            await farEnd.SendAsync("Probe-0");
            await stays.WhenReceived(1);
            subscription.Dispose();
            await farEnd.SendAsync("Probe-1");
            await stays.WhenReceived(2);

            gone.Types.Should().Equal(["Probe-0"], "disposing the subscription removes the observer");
        }
        finally
        {
            await QuietDisposeAsync(sut);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AriClient Client(AriFarEnd farEnd, bool autoReconnect = false) =>
        new(Options(farEnd.BaseUrl, autoReconnect), new CapturingLogger<AriClient>());

    private static IOptions<AriClientOptions> Options(string baseUrl, bool autoReconnect = false) =>
        Microsoft.Extensions.Options.Options.Create(new AriClientOptions
        {
            BaseUrl = baseUrl,
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(10),
        });

    private static async Task<Exception?> Record(Func<Task> action) => await Xunit.Record.ExceptionAsync(action);

    // A test's cleanup: on a tree where the disposal throws, the assertions report it, not the finally.
    private static async Task QuietDisposeAsync(AriClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (InvalidOperationException)
        {
            // Measured by the disposal tests.
        }
    }

    private static TaskStatus? ConsumerStatus(AriClient client) =>
        PumpField.GetValue(client) is AriEventPump pump && PumpConsumerField.GetValue(pump) is Task consumer
            ? consumer.Status
            : null;

    private static string Describe(AriClient client, Recorder observer)
    {
        var consumer = PumpField.GetValue(client) is AriEventPump pump ? PumpConsumerField.GetValue(pump) as Task : null;
        return $"received {observer.Calls} [{string.Join(", ", observer.Types)}], consumer {consumer?.Status.ToString() ?? "none"}"
            + $" ({consumer?.Exception?.InnerException?.Message ?? "no exception"}), state {client.State}";
    }

    /// <summary>
    /// An observer that records what it received and what it was called for, and throws where it was told to: from
    /// <c>OnNext</c> after counting the call and without recording the event, or from <c>OnCompleted</c>.
    /// </summary>
    private sealed class Recorder(
        Func<AriEvent, bool>? throwOn = null, Exception? failure = null, bool throwOnCompleted = false) : IObserver<AriEvent>
    {
        private readonly Lock _gate = new();
        private readonly List<string?> _types = [];
        private int _calls;
        private volatile bool _completed;

        public int Calls => Volatile.Read(ref _calls);

        public bool Completed => _completed;

        public IReadOnlyList<string?> Types
        {
            get { lock (_gate) return [.. _types]; }
        }

        public void OnNext(AriEvent value)
        {
            Interlocked.Increment(ref _calls);
            if (throwOn?.Invoke(value) == true)
                throw failure ?? new InvalidOperationException("observer failed");

            lock (_gate)
                _types.Add(value.Type);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
            _completed = true;
            if (throwOnCompleted)
                throw new InvalidOperationException("observer failed at the end of the stream");
        }

        public Task<bool> WhenCalled(int calls) => WaitAsync(() => Calls >= calls);

        public Task<bool> WhenReceived(int events) => WaitAsync(() => Types.Count >= events);

        public Task<bool> WhenSaw(string type) => WaitAsync(() => Types.Contains(type));

        private static async Task<bool> WaitAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow + WaitLimit;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return true;
                await Task.Delay(5); // fence-allow: LOOP-DRIVER — an observer has no signal for a delivery that never comes; bounded by WaitLimit
            }

            return predicate();
        }
    }

    /// <summary>Sums what is added to one counter of the ARI meter, matched by name, while it listens.</summary>
    private sealed class InstrumentSum : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public InstrumentSum(string name)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ari" && instrument.Name == name)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A logger whose provider fails on a Warning, as a broken sink would.</summary>
    private sealed class ThrowingOnWarningLogger : ILogger<AriClient>
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
