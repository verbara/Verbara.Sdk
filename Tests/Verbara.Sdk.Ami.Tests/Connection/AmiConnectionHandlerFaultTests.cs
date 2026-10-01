using System.Globalization;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// An <c>OnEvent</c> handler that fails — by throwing, or through the task it returns — is logged once at Warning with
/// the event's type, and the connection goes on delivering every later event. The dispatch returned the handler's task
/// unguarded to the event pump, whose consumer then ended: from that event on the session delivered nothing, silently,
/// and a loss's drain stopped at the failing event with the rest of the buffer undelivered.
/// </summary>
/// <remarks>
/// Every test but one uses one handler, so what it pins holds whichever shape the guard takes. The guard is one per
/// handler (the owner's ruling, 2026-09-30): <see cref="OnEvent_ShouldCallTheOtherHandlersAndObservers_WhenOneHandlerThrows"/>
/// pins what only that shape gives — the other handlers and the observers receive the event one handler threw on —
/// and <see cref="OnEvent_ShouldCountTheFault_WhenAHandlerThrows"/> the <c>ami.events.handler_faults</c> counter kept
/// with it. Deliveries are counted, never timed: the peer answers a Ping after the events it writes, and
/// the test waits for a sentinel event written last, which arrives only once everything before it has been dispatched.
/// Time enters only as <see cref="Bound"/>, a hang bound. The class runs in <see cref="AmiEventsDroppedMetricGroup"/>
/// because one test listens on the process-wide <c>ami.events.dropped</c> counter.
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed class AmiConnectionHandlerFaultTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>How many events the peer sends after the one the handler fails on.</summary>
    private const int Later = 10;

    /// <summary>How many events a lost connection still holds behind the dispatch that fails.</summary>
    private const int Buffered = 41;

    private const string HandlerFaultFormat = "[AMI_EVENT] OnEvent handler threw on {EventType}";

    [Fact]
    public async Task OnEvent_ShouldKeepDeliveringEvents_WhenAHandlerThrowsSynchronously()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false);
        var handler = new FailingHandler(failOn: 1, Failure.Throw);
        connection.OnEvent += handler.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WritePeersAsync(socket, 0, 1 + Later);
        (await socket.WriteEventAsync("PeerStatus", [new("Peer", Sentinel)])).Should().BeTrue();
        var delivered = await CompletesWithinBoundAsync(handler.SentinelReached);

        using (new AssertionScope())
        {
            delivered.Should().BeTrue("delivery goes on after the handler threw, up to the sentinel written last");
            handler.PeersAfterTheFailure().Should().Equal(Peers(1, Later),
                $"the handler receives all {Later} events after the one it threw on, in order");
            connection.State.Should().Be(AmiConnectionState.Connected);
        }
    }

    [Fact]
    public async Task OnEvent_ShouldKeepDeliveringEvents_WhenAHandlerReturnsAFaultedTask()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        using var drops = new BufferFullDrops();
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false);
        var handler = new FailingHandler(failOn: 1, Failure.FaultedTask);
        connection.OnEvent += handler.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WritePeersAsync(socket, 0, 1 + Later);
        (await socket.WriteEventAsync("PeerStatus", [new("Peer", Sentinel)])).Should().BeTrue();
        var delivered = await CompletesWithinBoundAsync(handler.SentinelReached);

        using (new AssertionScope())
        {
            delivered.Should().BeTrue("a faulted task is treated like a throw: delivery goes on, up to the sentinel");
            handler.PeersAfterTheFailure().Should().Equal(Peers(1, Later),
                $"the handler receives all {Later} events after the one whose task faulted, in order");
            drops.Total.Should().Be(0, "nothing is dropped as a full buffer");
        }
    }

    [Fact]
    public async Task OnEvent_ShouldLogTheFaultOnceAtWarning_WhenAHandlerThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new StateLogger();
        await using var connection = Create(factory, autoReconnect: false, logger);
        var handler = new FailingHandler(failOn: 1, Failure.Throw);
        connection.OnEvent += handler.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WritePeersAsync(socket, 0, 1 + Later);
        (await socket.WriteEventAsync("PeerStatus", [new("Peer", Sentinel)])).Should().BeTrue();
        await CompletesWithinBoundAsync(handler.SentinelReached);

        var faults = logger.Entries.Where(e => e.Format == HandlerFaultFormat).ToList();
        using (new AssertionScope())
        {
            faults.Should().ContainSingle("the one failure is logged once");
            faults.Should().OnlyContain(e => e.Level == LogLevel.Warning);
            faults.Select(e => e.State.GetValueOrDefault("EventType")).Should().Equal(["PeerStatus"],
                "the line names the type of the event the handler threw on");
            faults.Should().OnlyContain(e => e.Exception is InvalidOperationException, "the handler's exception is logged with it");
        }
    }

    /// <summary>
    /// Every failure is counted once on <c>ami.events.handler_faults</c>, beside its Warning, so a host sees a handler
    /// that keeps failing without reading its logs. The counter is process-wide; this class runs apart from every other
    /// test class (<see cref="AmiEventsDroppedMetricGroup"/>), so nothing but this test's handler records on it.
    /// </summary>
    [Fact]
    public async Task OnEvent_ShouldCountTheFault_WhenAHandlerThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        using var faults = new HandlerFaults();
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false);
        var handler = new FailingHandler(failOn: 1, Failure.Throw);
        connection.OnEvent += handler.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WritePeersAsync(socket, 0, 1 + Later);
        (await socket.WriteEventAsync("PeerStatus", [new("Peer", Sentinel)])).Should().BeTrue();
        var delivered = await CompletesWithinBoundAsync(handler.SentinelReached);

        using (new AssertionScope())
        {
            delivered.Should().BeTrue("delivery goes on after the handler threw, up to the sentinel written last");
            faults.Published.Should().BeTrue("the connection publishes the ami.events.handler_faults counter");
            faults.Total.Should().Be(1, "the one failure is counted once, and the events delivered after it are not");
        }
    }

    /// <summary>
    /// The first of two handlers throws synchronously on the event; the second handler and the observer still receive
    /// that event, and the next one. Before, the multicast stopped at the throw, so the second handler never saw the
    /// event, and the consumer ended, so no one saw anything after it.
    /// </summary>
    [Fact]
    public async Task OnEvent_ShouldCallTheOtherHandlersAndObservers_WhenOneHandlerThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false);
        var first = new FailingHandler(failOn: 1, Failure.Throw);
        var second = new Recorder();
        var observer = new Recorder();
        connection.OnEvent += first.HandleAsync;
        connection.OnEvent += second.HandleAsync;
        using var subscription = connection.Subscribe(observer);
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WritePeersAsync(socket, 0, 2);
        (await socket.WriteEventAsync("PeerStatus", [new("Peer", Sentinel)])).Should().BeTrue();
        var secondReached = await CompletesWithinBoundAsync(second.SentinelReached);
        var observerReached = await CompletesWithinBoundAsync(observer.SentinelReached);

        using (new AssertionScope())
        {
            secondReached.Should().BeTrue("the second handler receives every event up to the sentinel written last");
            observerReached.Should().BeTrue("the observer receives every event up to the sentinel written last");
            second.Peers().Should().Equal(Peers(0, 2),
                "the second handler receives the event the first one threw on, and the next one, in order");
            observer.Peers().Should().Equal(Peers(0, 2),
                "the observer receives the event the first handler threw on, and the next one, in order");
            first.PeersAfterTheFailure().Should().Equal(Peers(1, 1), "the handler that threw goes on receiving events");
        }
    }

    /// <summary>
    /// The peer closes with <see cref="Buffered"/> events behind the held first dispatch; when the gate opens, the
    /// handler throws. No caller ended the connection, so the loss's drain delivers the whole buffer, in order.
    /// </summary>
    [Fact]
    public async Task LostConnection_ShouldDeliverTheWholeBuffer_WhenAHandlerThrowsDuringTheDrain()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new StateLogger();
        await using var connection = Create(factory, autoReconnect: false, logger);
        var handler = new FailingHandler(failOn: 1, Failure.ThrowWhenReleased);
        connection.OnEvent += handler.HandleAsync;
        var disconnected = logger.Logged("[AMI] Disconnected");
        var socket = await ConnectAsync(connection, factory, peerCts);
        await WritePeersAsync(socket, 0, 1);
        (await CompletesWithinBoundAsync(handler.Held)).Should().BeTrue("the handler receives the first event and holds it");
        await WritePeersAsync(socket, 1, Buffered);
        await PingAsync(connection, socket, peerCts);

        socket.CloseFromPeer();
        handler.Release();
        var ended = await CompletesWithinBoundAsync(disconnected);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the peer's close ends the connection");
            handler.PeersAfterTheFailure().Should().Equal(Peers(1, Buffered),
                $"a loss delivers all {Buffered} buffered events after the one the handler threw on, in order, " +
                "before the lost connection reports Disconnected");
            logger.Entries.Where(e => e.Format == "[AMI_EVENT] Discarded on caller ending: count={Count}")
                .Should().BeEmpty("nothing is reported as discarded by a caller's ending");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private const string Sentinel = "SIP/sentinel";

    private static string PeerName(int i) => "SIP/" + i.ToString(CultureInfo.InvariantCulture);

    private static string[] Peers(int from, int count) => [.. Enumerable.Range(from, count).Select(PeerName)];

    private static async Task WritePeersAsync(PipedSocket socket, int from, int count)
    {
        for (var i = from; i < from + count; i++)
            (await socket.WriteEventAsync("PeerStatus", [new("Peer", PeerName(i))])).Should().BeTrue();
    }

    /// <summary>A Ping the peer answers: once it returns, every event written before the answer has been read.</summary>
    private static async Task PingAsync(AmiConnection connection, PipedSocket socket, CancellationTokenSource peerCts)
    {
        var ping = connection.SendActionAsync(new PingAction()).AsTask();
        var action = await socket.ReadActionAsync(peerCts.Token).WaitAsync(Bound);
        PipedSocket.IsPing(action).Should().BeTrue("the connection sends the Ping");
        (await socket.RespondAsync("Success", PipedSocket.ActionIdOf(action!))).Should().BeTrue();
        (await CompletesWithinBoundAsync(ping)).Should().BeTrue("the Ping is answered");
    }

    private static AmiConnection Create(PipedSocketFactory factory, bool autoReconnect, ILogger<AmiConnection>? logger = null) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
        }), factory, logger ?? new StateLogger());

    private static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        CancellationTokenSource peerCts)
    {
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
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

    private enum Failure
    {
        /// <summary>Throws from the handler itself, before it returns a task.</summary>
        Throw,

        /// <summary>Returns a task that has already faulted.</summary>
        FaultedTask,

        /// <summary>Holds the dispatch until <see cref="FailingHandler.Release"/>, then faults its task.</summary>
        ThrowWhenReleased,
    }

    /// <summary>
    /// An <c>OnEvent</c> handler that fails on its <c>failOn</c>-th dispatch and records the peer of every PeerStatus it
    /// receives after that one, in order; the sentinel completes <see cref="SentinelReached"/>.
    /// </summary>
    private sealed class FailingHandler(int failOn, Failure failure)
    {
        private readonly Lock _gate = new();
        private readonly List<string?> _after = [];
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task Held => _held.Task;

        public Task SentinelReached => _sentinel.Task;

        public void Release() => _release.TrySetResult();

        public IReadOnlyList<string?> PeersAfterTheFailure()
        {
            lock (_gate)
            {
                return [.. _after];
            }
        }

        public ValueTask HandleAsync(ManagerEvent evt)
        {
            var n = Interlocked.Increment(ref _count);
            if (n == failOn)
            {
                return failure switch
                {
                    Failure.Throw => throw new InvalidOperationException("The handler fails on this event."),
                    Failure.FaultedTask => ValueTask.FromException(new InvalidOperationException("The handler's task faults.")),
                    _ => HoldThenThrowAsync(),
                };
            }

            var peer = (evt as PeerStatusEvent)?.Peer;
            if (peer == Sentinel)
            {
                _sentinel.TrySetResult();
                return ValueTask.CompletedTask;
            }

            if (n > failOn)
            {
                lock (_gate)
                {
                    _after.Add(peer);
                }
            }

            return ValueTask.CompletedTask;
        }

        private async ValueTask HoldThenThrowAsync()
        {
            _held.TrySetResult();
            await _release.Task;
            throw new InvalidOperationException("The handler fails while the loss drains the buffer.");
        }
    }

    /// <summary>
    /// An <c>OnEvent</c> handler and an observer at once, that never fails: records the peer of every PeerStatus before
    /// the sentinel, in order; the sentinel completes <see cref="SentinelReached"/>.
    /// </summary>
    private sealed class Recorder : IObserver<ManagerEvent>
    {
        private readonly Lock _gate = new();
        private readonly List<string?> _peers = [];
        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SentinelReached => _sentinel.Task;

        public IReadOnlyList<string?> Peers()
        {
            lock (_gate)
            {
                return [.. _peers];
            }
        }

        public ValueTask HandleAsync(ManagerEvent evt)
        {
            OnNext(evt);
            return ValueTask.CompletedTask;
        }

        public void OnNext(ManagerEvent value)
        {
            var peer = (value as PeerStatusEvent)?.Peer;
            if (peer == Sentinel)
            {
                _sentinel.TrySetResult();
                return;
            }

            lock (_gate)
            {
                _peers.Add(peer);
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed record Entry(LogLevel Level, string? Format, string Line, IReadOnlyDictionary<string, object?> State,
        Exception? Exception);

    /// <summary>A logger that keeps every entry with its format and state, and signals a line once it is logged.</summary>
    private sealed class StateLogger : ILogger<AmiConnection>
    {
        private readonly Lock _gate = new();
        private readonly List<Entry> _entries = [];
        private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public Task Logged(string fragment)
        {
            lock (_gate)
            {
                if (_entries.Exists(e => e.Line.Contains(fragment, StringComparison.Ordinal)))
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((fragment, signal));
                return signal.Task;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    values[pair.Key] = pair.Value;
            }

            var line = formatter(state, exception);
            var format = values.TryGetValue("{OriginalFormat}", out var f) ? f as string : null;
            List<TaskCompletionSource> fired = [];
            lock (_gate)
            {
                _entries.Add(new Entry(logLevel, format, line, values, exception));
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (line.Contains(_waiters[i].Fragment, StringComparison.Ordinal))
                    {
                        fired.Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var signal in fired)
                signal.TrySetResult();
        }
    }

    /// <summary>Every event dropped as a full buffer, process-wide, while this capture is alive.</summary>
    private sealed class BufferFullDrops : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private long _total;

        public BufferFullDrops()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ami" && instrument.Name == "ami.events.dropped")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value as string == "buffer_full")
                        Interlocked.Add(ref _total, value);
                }
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Every handler fault counted on <c>ami.events.handler_faults</c>, process-wide, while this capture is alive.</summary>
    private sealed class HandlerFaults : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private long _total;
        private int _published;

        public HandlerFaults()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ami" && instrument.Name == "ami.events.handler_faults")
                {
                    Interlocked.Exchange(ref _published, 1);
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public bool Published => Volatile.Read(ref _published) == 1;

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }
}
