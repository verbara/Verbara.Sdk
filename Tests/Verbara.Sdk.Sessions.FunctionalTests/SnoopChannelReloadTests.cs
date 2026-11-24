using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The collection the snoop reload tests run in. Apply with <c>[Collection(SnoopChannelReloadGroup.Name)]</c>.
/// </summary>
/// <remarks>
/// The session counters are process-wide statics with no tags, and the reload admits and removes channels on the
/// reload's own thread, so a capture cannot tell this class's measurements apart by thread. The collection is declared
/// with <c>DisableParallelization</c>: xunit runs it only after every parallel collection has finished, so nothing else
/// records while one of its tests listens and the tests can assert exact counts.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SnoopChannelReloadGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "sessions-snoop-channel-reload";
}

/// <summary>
/// A snoop channel that a state reload reports opens no call: the snapshot route reaches the same arrival handler a
/// live <c>Newchannel</c> does, and the later reload that no longer lists it reaches the same removal handler.
///
/// <para>The snoop carries the shape measured on Asterisk 22.12.0, 23.6.0 and 24.0.0 for a real ARI snoop: the name
/// <c>Snoop/&lt;target uniqueid&gt;-&lt;n&gt;</c> and a linkedid that is its own uniqueid. The positive control runs the
/// identical two reloads on an ordinary <c>PJSIP/</c> entry.</para>
///
/// <para>Harness traps inherited from <see cref="ReloadedSessionStateTests"/>: the status reply is armed before the
/// reconnect is raised; <c>StartAsync</c> is where <c>Reconnected</c> is attached; and <c>OnReconnected</c> is
/// <c>async void</c>, so its log lines are the only completion signal it produces.</para>
/// </summary>
[Collection(SnoopChannelReloadGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SnoopChannelReloadTests : IAsyncLifetime
{
    private const string ServerId = "test-srv";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly ReloadLogger _serverLog = new();

    private readonly ConcurrentQueue<SessionDomainEvent> _sessionEvents = new();
    private readonly IDisposable _sessionSubscription;

    private IReadOnlyList<StatusEvent> _statusReply = [];

    public SnoopChannelReloadTests()
    {
        _connection.AsteriskVersion.Returns("22.0.0");
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0)));

        _server = new VerbaraServer(_connection, _serverLog.For<VerbaraServer>());
        _sessions = new CallSessionManager(
            Options.Create(new SessionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());

        _sessions.AttachToServer(_server, ServerId);
        _sessionSubscription = _sessions.Events.Subscribe(_sessionEvents.Enqueue);
    }

    [Fact]
    public async Task Reload_ShouldOpenNoCall_WhenTheSnapshotListsASnoopWithItsOwnLinkedIdAndALaterReloadDoesNot()
    {
        const string snoop = "1794794993.11";
        await _server.StartAsync();
        using var meters = new SessionMeterCapture(_sessions);

        await WhenTheConnectionReconnects(Entry(snoop, "Snoop/1794794992.7-00000001", linkedId: snoop));
        var listed = Read(snoop, meters);
        await WhenTheConnectionReconnects();
        var unlisted = Read(snoop, meters);

        new { Listed = listed, Unlisted = unlisted }.Should().BeEquivalentTo(
            new { Listed = Nothing with { Held = true }, Unlisted = Nothing },
            "a snoop is a tap Asterisk creates on a call, not a call, whichever route reports it: a reload that lists "
            + "it opens no session, publishes no call event and moves no session meter, and the reload that drops it "
            + $"ends nothing. Server log:{Environment.NewLine}{_serverLog}");
    }

    /// <summary>Positive control: the identical two reloads on an ordinary channel open one call and end it once.</summary>
    [Fact]
    public async Task Reload_ShouldOpenAndEndExactlyOneCall_WhenTheSnapshotListsAnOrdinaryChannelAndALaterReloadDoesNot()
    {
        const string leg = "1794794993.13";
        await _server.StartAsync();
        using var meters = new SessionMeterCapture(_sessions);

        await WhenTheConnectionReconnects(Entry(leg, "PJSIP/trunk-00000001", linkedId: leg));
        var listed = Read(leg, meters);
        await WhenTheConnectionReconnects();
        var unlisted = Read(leg, meters);

        new { Listed = listed, Unlisted = unlisted }.Should().BeEquivalentTo(
            new
            {
                Listed = new Reading(Held: true, FoundByChannel: true, FoundByLinkedId: true, Active: 1,
                    ActiveGauge: 1, Started: 1, Endings: 0, Created: 1, EndingsCounted: 0),
                Unlisted = new Reading(Held: false, FoundByChannel: false, FoundByLinkedId: true, Active: 0,
                    ActiveGauge: 0, Started: 1, Endings: 1, Created: 1, EndingsCounted: 1),
            },
            "the same two reloads on an ordinary channel open one call and end it once: this is what the snoop case "
            + $"would read if the snoop were treated as a call. Server log:{Environment.NewLine}{_serverLog}");
    }

    // --- helpers ----------------------------------------------------------------------------------------------------

    private static readonly Reading Nothing = new(Held: false, FoundByChannel: false, FoundByLinkedId: false,
        Active: 0, ActiveGauge: 0, Started: 0, Endings: 0, Created: 0, EndingsCounted: 0);

    private Reading Read(string uniqueId, SessionMeterCapture meters) => new(
        _server.Channels.GetByUniqueId(uniqueId) is not null,
        _sessions.GetByChannelId(uniqueId) is not null,
        _sessions.GetByLinkedId(uniqueId) is not null,
        _sessions.ActiveSessions.Count(),
        meters.ReadActiveGauge(),
        _sessionEvents.OfType<CallStartedEvent>().Count(),
        _sessionEvents.OfType<CallEndedEvent>().Count(),
        meters.Get("sessions.created"),
        meters.Get("sessions.completed") + meters.Get("sessions.failed"));

    /// <summary>A channel in state Up as <c>Status</c> reports it: the state in the raw <c>ChannelState</c> header.</summary>
    private static StatusEvent Entry(string uniqueId, string channel, string linkedId) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        LinkedId = linkedId,
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Context"] = "from-trunk",
            ["ChannelState"] = ((int)ChannelState.Up).ToString(CultureInfo.InvariantCulture),
            ["ChannelStateDesc"] = nameof(ChannelState.Up),
        },
    };

    /// <summary>Answers the three actions the load sends; only <c>Status</c> carries channels.</summary>
    private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();

        if (action is AgentsAction)
        {
            _serverLog.ReloadFinished.TrySetResult();
            yield break;
        }

        if (action is not StatusAction)
            yield break;

        foreach (var status in _statusReply)
            yield return status;
    }

    /// <summary>
    /// Raises <c>Reconnected</c> and waits for the reload to end — at its last action when it completed, at the
    /// catch-all's log line when it failed.
    /// </summary>
    private async Task WhenTheConnectionReconnects(params StatusEvent[] channelsAsteriskHas)
    {
        _statusReply = channelsAsteriskHas;
        _serverLog.RearmReloadSignals();
        _connection.Reconnected += Raise.Event<Action>();

        // async void OnReconnected cannot be awaited, so the wait is on its two log-line signals.
        // fence-allow: GUARD-TIMEOUT — bounds that wait so a reload that never ran reports the log
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var ended = await Task.WhenAny(_serverLog.ReloadFinished.Task, _serverLog.ReloadFailed.Task, timeout);

        ReferenceEquals(ended, timeout).Should().BeFalse(
            $"the reconnect reload neither finished nor logged a failure. Server log:{Environment.NewLine}{_serverLog}");
        ReferenceEquals(ended, _serverLog.ReloadFailed.Task).Should().BeFalse(
            $"premise: the reload completed. Server log:{Environment.NewLine}{_serverLog}");
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _sessionSubscription.Dispose();
        await _sessions.DisposeAsync();
        await _server.DisposeAsync();
    }

    private sealed record Reading(
        bool Held,
        bool FoundByChannel,
        bool FoundByLinkedId,
        int Active,
        long? ActiveGauge,
        int Started,
        int Endings,
        long Created,
        long EndingsCounted);

    /// <summary>
    /// The process-wide session counters while it listens (the class runs in a non-parallel collection, so they move
    /// only for its own channels), plus this manager's own active-sessions gauge.
    /// </summary>
    private sealed class SessionMeterCapture : IDisposable
    {
        private readonly MeterListener _counters = new();
        private readonly MeterListener _gauges = new();
        private readonly ConcurrentDictionary<string, long> _sums = new(StringComparer.Ordinal);
        private long? _active;

        public SessionMeterCapture(CallSessionManager manager)
        {
            // Read before the listener starts: a first read of SessionMetrics runs its static initializer, which
            // publishes the instruments while the field is still null.
            var meter = SessionMetrics.Meter;
            var residency = manager.ResidencyMeter;

            _counters.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _counters.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                _sums.AddOrUpdate(instrument.Name, value, (_, sum) => sum + value));
            _counters.Start();

            _gauges.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, residency) && instrument.Name == "sessions.active")
                    listener.EnableMeasurementEvents(instrument);
            };
            _gauges.SetMeasurementEventCallback<long>((_, value, _, _) => _active = value);
            _gauges.Start();
        }

        public long Get(string name) => _sums.GetValueOrDefault(name);

        public long? ReadActiveGauge()
        {
            _active = null;
            _gauges.RecordObservableInstruments();
            return _active;
        }

        public void Dispose()
        {
            _gauges.Dispose();
            _counters.Dispose();
        }
    }

    /// <summary>
    /// Records every line the server logs and completes one of two signals when a reload ends: the last action for a
    /// reload that finished, the catch-all's line for one that failed. Both are rearmed per reload, because
    /// <c>StartAsync</c>'s own load trips the first one.
    /// </summary>
    private sealed class ReloadLogger
    {
        private readonly List<string> _lines = [];

        public TaskCompletionSource ReloadFinished { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReloadFailed { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RearmReloadSignals()
        {
            ReloadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReloadFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ILogger<T> For<T>() => new Sink<T>(this);

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }

            if (line.Contains("Reconnect reload failed", StringComparison.Ordinal))
                ReloadFailed.TrySetResult();
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.Count == 0
                    ? "  (the server logged nothing)"
                    : "  " + string.Join($"{Environment.NewLine}  ", _lines);
            }
        }

        private sealed class Sink<T>(ReloadLogger owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                owner.Add(exception is null
                    ? $"[{logLevel}] {message}"
                    : $"[{logLevel}] {message} -> {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
