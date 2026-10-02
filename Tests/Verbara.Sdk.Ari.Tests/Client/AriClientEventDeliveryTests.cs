using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Verbara.Sdk.Ari.Audio;
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
/// What an ARI client delivers from the events it has buffered when its caller ends it, and how what it does not
/// deliver is counted. A caller's <c>DisposeAsync</c> or <c>DisconnectAsync</c> delivers no buffered event after the
/// dispatch in progress, waits for that dispatch only, and reports the rest once: one Warning and one measurement
/// on <c>ari.events.dropped</c> tagged <c>reason=caller_ending</c>. A loss nobody asked for still delivers every
/// buffered event, in order. The buffer belongs to one connection, with one consumer. A full buffer's discards are
/// counted, tagged <c>reason=buffer_full</c>.
/// </summary>
/// <remarks>
/// <para>
/// Measured before the rule (20 and 200 buffered events behind a held dispatch, 10 runs each): a disposal delivered
/// every one of them after it was called and waited for all of them, 5.0 s for 20 at 250 ms each and 50.0 s for 200;
/// a disconnect returned at once while every buffered event kept reaching the observers; and a client connected
/// again after a disconnect read its buffer with two consumers, out of order in half of the runs.
/// </para>
/// <para>
/// A dispatch is held on a gate inside the observer's first <c>OnNext</c>. The gate is opened only once the client's
/// event pump has been told to stop, read through reflection on the pump's private <c>_cts</c>, so a delivery after
/// that point is one the rule forbids, never one that raced the ending. The pump is reached through the client's
/// private <c>_pump</c> field, also by reflection. Every wait is bounded by <see cref="WaitLimit"/>.
/// </para>
/// </remarks>
[Collection(AriEventsDroppedGroup.Name)]
public sealed class AriClientEventDeliveryTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private const int Buffered = 20;

    private static readonly FieldInfo PumpField =
        typeof(AriClient).GetField("_pump", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PumpStopField =
        typeof(AriEventPump).GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PumpConsumerField =
        typeof(AriEventPump).GetField("_consumerTask", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task DisposeAsync_ShouldDeliverNoneOfTheBufferedEvents_WhenAnObserverIsHeld()
    {
        await using var peer = new EventPeer(Buffered + 1);
        using var observer = new GatedObserver();
        var sut = new AriClient(Options(peer.Port), new RecordingLogger());
        using var subscription = sut.Subscribe(observer);
        await sut.ConnectAsync();
        var pump = await HeldWithBufferedAsync(sut, observer, Buffered);

        observer.MarkEnding();
        var dispose = sut.DisposeAsync().AsTask();
        var stopped = await StopRequestedAsync(pump, dispose);
        var returnedWhileHeld = dispose.IsCompleted;
        observer.Open();
        await dispose.WaitAsync(WaitLimit);

        using (new AssertionScope())
        {
            stopped.Should().BeTrue("the disposal tells the pump to stop");
            returnedWhileHeld.Should().BeFalse("the disposal waits for the dispatch in progress");
            observer.AfterEnding.Should().Be(0, "no buffered event is delivered after the caller's ending");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReportTheDroppedEventsOnce_WhenEventsAreBuffered()
    {
        using var dropped = new DroppedCapture();
        await using var peer = new EventPeer(Buffered + 1);
        using var observer = new GatedObserver();
        var logger = new RecordingLogger();
        var sut = new AriClient(Options(peer.Port), logger);
        using var subscription = sut.Subscribe(observer);
        await sut.ConnectAsync();
        var pump = await HeldWithBufferedAsync(sut, observer, Buffered);

        var dispose = sut.DisposeAsync().AsTask();
        await StopRequestedAsync(pump, dispose);
        observer.Open();
        await dispose.WaitAsync(WaitLimit);

        using (new AssertionScope())
        {
            dropped.Tagged("caller_ending").Should().Equal([(long)Buffered], "one measurement carries the count");
            logger.Entries.Where(e => e.EventId.Name == "EventsDiscardedOnCallerEnding")
                .Should().ContainSingle("the count is logged once").Which.Level.Should().Be(LogLevel.Warning);
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReportNothing_WhenNothingIsBuffered()
    {
        // Control: nothing buffered, nothing reported, with or without the rule.
        using var dropped = new DroppedCapture();
        await using var peer = new EventPeer(1);
        using var observer = new GatedObserver();
        var logger = new RecordingLogger();
        var sut = new AriClient(Options(peer.Port), logger);
        using var subscription = sut.Subscribe(observer);
        await sut.ConnectAsync();
        await HeldWithBufferedAsync(sut, observer, 0);

        var dispose = sut.DisposeAsync().AsTask();
        observer.Open();
        await dispose.WaitAsync(WaitLimit);

        using (new AssertionScope())
        {
            dropped.Tagged("caller_ending").Should().BeEmpty();
            logger.Entries.Should().NotContain(e => e.EventId.Name == "EventsDiscardedOnCallerEnding");
        }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldDeliverNoneOfTheBufferedEvents_WhenAnObserverIsHeld()
    {
        using var dropped = new DroppedCapture();
        await using var peer = new EventPeer(Buffered + 1);
        using var observer = new GatedObserver();
        var logger = new RecordingLogger();
        var sut = new AriClient(Options(peer.Port), logger);
        using var subscription = sut.Subscribe(observer);
        try
        {
            await sut.ConnectAsync();
            var pump = await HeldWithBufferedAsync(sut, observer, Buffered);

            observer.MarkEnding();
            var disconnect = sut.DisconnectAsync().AsTask();
            var stopped = await StopRequestedAsync(pump, disconnect);
            var returnedWhileHeld = disconnect.IsCompleted;
            observer.Open();
            await disconnect.WaitAsync(WaitLimit);
            await WaitForAsync(() => observer.Total == Buffered + 1 || ((Task?)PumpConsumerField.GetValue(pump))?.IsCompleted == true);

            using (new AssertionScope())
            {
                stopped.Should().BeTrue("the disconnect releases the connection's buffer");
                returnedWhileHeld.Should().BeFalse("the disconnect waits for the dispatch in progress");
                observer.AfterEnding.Should().Be(0, "no buffered event is delivered after the caller's disconnect");
                dropped.Tagged("caller_ending").Should().Equal([(long)Buffered]);
                logger.Entries.Should().ContainSingle(e => e.EventId.Name == "EventsDiscardedOnCallerEnding");
            }
        }
        finally
        {
            observer.Open();
            await sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotReportTheDropsAgain_WhenTheClientWasDisconnectedFirst()
    {
        using var dropped = new DroppedCapture();
        await using var peer = new EventPeer(Buffered + 1);
        using var observer = new GatedObserver();
        var logger = new RecordingLogger();
        var sut = new AriClient(Options(peer.Port), logger);
        using var subscription = sut.Subscribe(observer);
        await sut.ConnectAsync();
        var pump = await HeldWithBufferedAsync(sut, observer, Buffered);

        observer.MarkEnding();
        var disconnect = sut.DisconnectAsync().AsTask();
        await StopRequestedAsync(pump, disconnect);
        var dispose = sut.DisposeAsync().AsTask();
        observer.Open();
        await disconnect.WaitAsync(WaitLimit);
        await dispose.WaitAsync(WaitLimit);

        using (new AssertionScope())
        {
            dropped.Tagged("caller_ending").Should().Equal(
                [(long)Buffered - observer.AfterEnding], "the drops are reported once in total, with what was not delivered");
            logger.Entries.Where(e => e.EventId.Name == "EventsDiscardedOnCallerEnding").Should().ContainSingle();
            observer.AfterEnding.Should().Be(0);
        }
    }

    [Fact]
    public async Task EventLoop_ShouldDeliverEveryBufferedEventInOrder_WhenTheConnectionIsLost()
    {
        // Control: a loss nobody asked for keeps delivering the buffer, in the order received.
        await using var peer = new EventPeer(Buffered + 1, closeAfterSending: true);
        using var observer = new GatedObserver();
        var logger = new RecordingLogger();
        var sut = new AriClient(Options(peer.Port, autoReconnect: true), logger);
        using var subscription = sut.Subscribe(observer);
        try
        {
            await sut.ConnectAsync();
            await HeldWithBufferedAsync(sut, observer, Buffered);

            peer.Close();
            (await WaitForAsync(() => sut.State == AriConnectionState.Reconnecting)).Should().BeTrue("the socket dropped");
            observer.Open();
            var all = await WaitForAsync(() => observer.Total == Buffered + 1);

            using (new AssertionScope())
            {
                all.Should().BeTrue("every buffered event is delivered after a loss");
                observer.Indexes.Should().Equal(Enumerable.Range(0, Buffered + 1), "in the order the far end sent them");
                logger.Entries.Should().NotContain(e => e.EventId.Name == "EventsDiscardedOnCallerEnding");
            }
        }
        finally
        {
            observer.Open();
            await sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldReadTheNewConnectionWithOneConsumer_WhenCalledAgainAfterADisconnect()
    {
        const int events = 50;
        await using var peer = new EventPeer(0, secondConnectionEvents: events);
        using var observer = new GatedObserver(holdFirst: false);
        var sut = new AriClient(Options(peer.Port), new RecordingLogger());
        using var subscription = sut.Subscribe(observer);
        try
        {
            await sut.ConnectAsync();
            var firstConsumer = ConsumerOf(sut);
            await sut.DisconnectAsync();
            await sut.ConnectAsync();
            var secondConsumer = ConsumerOf(sut);
            var all = await WaitForAsync(() => observer.Total == events);
            var firstEnded = await Task.WhenAny(firstConsumer, Task.Delay(WaitLimit)) == firstConsumer; // fence-allow: GUARD-TIMEOUT — bounds a wait on a task that should already have ended

            using (new AssertionScope())
            {
                firstEnded.Should().BeTrue("the first connection's consumer ended with its disconnect");
                secondConsumer.IsCompleted.Should().BeFalse("the new connection is read by its own consumer");
                all.Should().BeTrue();
                observer.Indexes.Should().Equal(Enumerable.Range(0, events), "each event once, in order");
            }
        }
        finally
        {
            await sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task EventLoop_ShouldCountEachDiscardTaggedBufferFull_WhenTheBufferIsFull()
    {
        // The buffer holds AriEventPump.DefaultCapacity events; three more than that arrive behind a held dispatch,
        // and the buffer discards its three oldest.
        const int overflow = 3;
        using var dropped = new DroppedCapture(expectedBufferFull: overflow);
        await using var peer = new EventPeer(1 + AriEventPump.DefaultCapacity + overflow);
        using var observer = new GatedObserver();
        var sut = new AriClient(Options(peer.Port), new RecordingLogger());
        using var subscription = sut.Subscribe(observer);
        try
        {
            await sut.ConnectAsync();
            await observer.Held.WaitAsync(WaitLimit);
            var counted = await Task.WhenAny(dropped.BufferFullReached, Task.Delay(WaitLimit)) == dropped.BufferFullReached; // fence-allow: GUARD-TIMEOUT — bounds the wait for the counter

            using (new AssertionScope())
            {
                counted.Should().BeTrue("each discarded event is counted");
                dropped.Tagged("buffer_full").Should().Equal(Enumerable.Repeat(1L, overflow), "one measurement per discarded event");
            }
        }
        finally
        {
            observer.Open();
            await sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryEnqueue_ShouldNotCountBufferFull_WhenThePumpWasReleasedByTheCallersEnding()
    {
        // Control: a write refused by a released pump is not a full buffer.
        using var dropped = new DroppedCapture();
        await using var peer = new EventPeer(0);
        var sut = new AriClient(Options(peer.Port), new RecordingLogger());
        await sut.ConnectAsync();
        var pump = (AriEventPump)PumpField.GetValue(sut)!;
        await sut.DisposeAsync();

        pump.TryEnqueue(new AriEvent { Type = "Late" });

        dropped.Tagged("buffer_full").Should().BeEmpty();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IOptions<AriClientOptions> Options(int port, bool autoReconnect = false) =>
        Microsoft.Extensions.Options.Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
            AutoReconnect = autoReconnect,
            // A loss waits in the backoff instead of dialling: these tests are about the buffer, not the redial.
            ReconnectInitialDelay = TimeSpan.FromMinutes(10),
            ReconnectMaxDelay = TimeSpan.FromMinutes(10),
        });

    /// <summary>
    /// Waits for the observer's first dispatch to be held and <paramref name="buffered"/> events to be pending
    /// behind it, and returns the connection's pump.
    /// </summary>
    private static async Task<AriEventPump> HeldWithBufferedAsync(AriClient sut, GatedObserver observer, int buffered)
    {
        await observer.Held.WaitAsync(WaitLimit);
        var pump = (AriEventPump)PumpField.GetValue(sut)!;
        (await WaitForAsync(() => pump.PendingCount == buffered)).Should().BeTrue($"{buffered} events are buffered");
        return pump;
    }

    /// <summary>
    /// Waits until the pump has been told to stop, or the ending has returned without telling it. True when it was
    /// told to stop.
    /// </summary>
    private static async Task<bool> StopRequestedAsync(AriEventPump pump, Task ending)
    {
        var stop = (CancellationTokenSource)PumpStopField.GetValue(pump)!;
        await WaitForAsync(() => IsCancellationRequested(stop) || ending.IsCompleted);
        return IsCancellationRequested(stop);
    }

    private static bool IsCancellationRequested(CancellationTokenSource source)
    {
        try
        {
            return source.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>The consumer reading the client's current buffer; a completed task when there is none.</summary>
    private static Task ConsumerOf(AriClient sut) =>
        PumpField.GetValue(sut) is AriEventPump pump && PumpConsumerField.GetValue(pump) is Task consumer
            ? consumer
            : Task.CompletedTask;

    private static async Task<bool> WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(5); // fence-allow: LOOP-DRIVER — the client exposes no signal for its buffer's state; bounded by WaitLimit
        }

        return predicate();
    }

    /// <summary>
    /// An observer whose first <c>OnNext</c> is held until <see cref="Open"/>. It counts every event, the events it
    /// received after <see cref="MarkEnding"/>, and keeps the index each event carries, in arrival order.
    /// </summary>
    private sealed class GatedObserver(bool holdFirst = true) : IObserver<AriEvent>, IDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new();
        private readonly List<int> _indexes = [];
        private readonly Lock _lock = new();
        private int _total;
        private int _afterEnding;
        private volatile bool _ending;

        public Task Held => _held.Task;

        public int Total => Volatile.Read(ref _total);

        public int AfterEnding => Volatile.Read(ref _afterEnding);

        public IReadOnlyList<int> Indexes
        {
            get { lock (_lock) return [.. _indexes]; }
        }

        public void MarkEnding() => _ending = true;

        public void Open() => _gate.Set();

        public void OnNext(AriEvent value)
        {
            var count = Interlocked.Increment(ref _total);
            lock (_lock)
                _indexes.Add(int.Parse(value.Type!.AsSpan("Probe-".Length), CultureInfo.InvariantCulture));

            if (count == 1 && holdFirst)
            {
                _held.TrySetResult();
                _gate.Wait(WaitLimit * 3);
                return;
            }

            if (_ending)
                Interlocked.Increment(ref _afterEnding);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        public void Dispose()
        {
            _gate.Set();
            _gate.Dispose();
        }
    }

    /// <summary>Keeps every measurement of <c>ari.events.dropped</c>, by its <c>reason</c> tag.</summary>
    private sealed class DroppedCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string? Reason, long Value)> _measurements = [];
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _bufferFull = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedBufferFull;

        public DroppedCapture(int expectedBufferFull = int.MaxValue)
        {
            _expectedBufferFull = expectedBufferFull;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ari" && instrument.Name == "ari.events.dropped")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.Start();
        }

        /// <summary>Completes once the expected number of <c>buffer_full</c> measurements has been recorded.</summary>
        public Task BufferFullReached => _bufferFull.Task;

        public IReadOnlyList<long> Tagged(string reason)
        {
            lock (_gate)
                return [.. _measurements.Where(m => m.Reason == reason).Select(m => m.Value)];
        }

        public void Dispose() => _listener.Dispose();

        private void OnMeasurement(
            Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                    reason = tag.Value as string;
            }

            lock (_gate)
            {
                _measurements.Add((reason, measurement));
                if (_measurements.Count(m => m.Reason == "buffer_full") >= _expectedBufferFull)
                    _bufferFull.TrySetResult();
            }
        }
    }

    /// <summary>Keeps what the client logged.</summary>
    private sealed class RecordingLogger : ILogger<AriClient>
    {
        private readonly List<(LogLevel Level, EventId EventId)> _entries = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<(LogLevel Level, EventId EventId)> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((logLevel, eventId));
        }
    }

    /// <summary>
    /// The Asterisk side: answers each dial and sends events <c>Probe-0</c>, <c>Probe-1</c>, … on it — the first dial
    /// <c>events</c> of them, the second <c>secondConnectionEvents</c> — then answers the client's close. With
    /// <c>closeAfterSending</c> the first connection is instead dropped when the test calls <see cref="Close"/>.
    /// </summary>
    private sealed class EventPeer : IAsyncDisposable
    {
        private readonly TcpListener _server = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _events;
        private readonly int _secondEvents;
        private readonly bool _closeAfterSending;
        private readonly Task _run;

        public EventPeer(int events, bool closeAfterSending = false, int secondConnectionEvents = 0)
        {
            _events = events;
            _secondEvents = secondConnectionEvents;
            _closeAfterSending = closeAfterSending;
            _server.Start();
            Port = ((IPEndPoint)_server.LocalEndpoint).Port;
            _run = Task.Run(RunAsync);
        }

        public int Port { get; }

        public void Close() => _close.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _server.Stop();
            await _run.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            var ct = _stop.Token;
            List<Task> serving = [];
            try
            {
                for (var dial = 1; !ct.IsCancellationRequested; dial++)
                {
                    var connection = await _server.AcceptTcpClientAsync(ct);
                    var events = dial switch { 1 => _events, 2 => _secondEvents, _ => -1 };
                    serving.Add(ServeAsync(connection, events, _closeAfterSending && dial == 1, ct));
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed.
            }
            catch (SocketException)
            {
                // The listener was stopped under the accept.
            }

            foreach (var task in serving)
                await task.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        private async Task ServeAsync(TcpClient connection, int events, bool closeAfterSending, CancellationToken ct)
        {
            using var held = connection;
            if (events < 0)
            {
                // A reconnect dial: held unanswered until disposal.
                await Task.Delay(Timeout.Infinite, ct); // fence-allow: GUARD-TIMEOUT — Timeout.Infinite; disposal is the only arm
                return;
            }

            var stream = connection.GetStream();
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, ct);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, ct);
            using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            for (var i = 0; i < events; i++)
            {
                var json = Encoding.UTF8.GetBytes($$"""{"type":"Probe-{{i}}","application":"test-app"}""");
                await ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
            }

            if (closeAfterSending)
            {
                await _close.Task.WaitAsync(ct);
                connection.Client.LingerState = new LingerOption(true, 0);
                return;
            }

            var buffer = new byte[256];
            try
            {
                while (true)
                {
                    var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        return;
                    }
                }
            }
            catch (WebSocketException)
            {
                // The client aborted the socket instead of closing it.
            }
            catch (IOException)
            {
                // The same, seen from the stream.
            }
        }
    }
}
