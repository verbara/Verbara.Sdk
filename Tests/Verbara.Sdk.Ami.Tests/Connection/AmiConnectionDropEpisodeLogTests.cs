using System.Diagnostics.Metrics;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A full event buffer logs an overflow as an episode: one <c>Warning</c> at its first refused event, <c>Debug</c> for
/// every later refusal of the same episode, and one summary <c>Warning</c> with the episode's count when the buffer
/// accepts again, or when the buffer is released while the episode is still open. <c>ami.events.dropped</c> keeps
/// counting every refused event.
/// </summary>
/// <remarks>
/// <para>
/// Every test holds a dispatch on a gate and fills the buffer behind it (capacity <see cref="Capacity"/>), so the events
/// written after are refused. A Ping answered by the peer is the fence: once it returns, the reader has handed every
/// earlier event to the buffer, and logged whatever that hand-over logs. No wall clock is read except
/// <see cref="Bound"/>, a hang bound every wait ends long before.
/// </para>
/// <para>
/// The class runs in <see cref="AmiEventsDroppedMetricGroup"/> because it listens on the process-wide
/// <c>ami.events.dropped</c> counter.
/// </para>
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed class AmiConnectionDropEpisodeLogTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const int Capacity = 2;

    private const string DroppedPrefix = "[AMI_EVENT] Dropped";

    private const string FirstDropFormat = "[AMI_EVENT] Dropped: event_type={EventType} channel={Channel}";

    [Fact]
    public async Task EventDropped_ShouldLogOneWarningAtTheFirstDropAndOneSummaryWithTheCount_WhenAnEpisodeOfNDropsEnds()
    {
        const int dropped = 5;
        using var drops = new BufferFullDrops();
        await using var rig = await EpisodeRig.StartAsync(autoReconnect: false);
        await rig.HoldAndFillAsync("HOLD/1");
        await rig.DropAsync("E1", dropped);

        rig.Open("HOLD/1");
        await rig.CountReachedAsync(1 + Capacity);
        (await rig.Peer.WriteEventAsync("VarSet", [new("Channel", "PJSIP/accepted-1"), new("Variable", "X"), new("Value", "1")])).Should().BeTrue();
        await rig.FenceAsync();

        var warnings = rig.Logger.DropLines(LogLevel.Warning);
        using (new AssertionScope())
        {
            drops.Total.Should().Be(dropped, "the metric counts every refused event");
            warnings.Should().HaveCount(2,
                $"an episode of {dropped} refused events logs one Warning at its first refusal and one summary when the " +
                "buffer accepts again, not one Warning per event");
            if (warnings.Count == 2)
            {
                warnings[0].Format.Should().Be(FirstDropFormat);
                warnings[0].Values.GetValueOrDefault("Channel").Should().Be("PJSIP/E1-0", "the first Warning names the first refused event");
                CountOf(warnings[1]).Should().Be(dropped, "the summary carries the episode's count");
                warnings[1].Values.Should().ContainKey("Ms", "the summary carries how long the episode lasted");
            }

            rig.Logger.DropLines(LogLevel.Debug).Should().HaveCount(dropped - 1,
                "every refusal after the first of the episode is logged at Debug");
        }
    }

    [Fact]
    public async Task EventDropped_ShouldLogTwoPairsInOrder_WhenTwoEpisodesAreSeparatedByAnAcceptedEvent()
    {
        using var drops = new BufferFullDrops();
        await using var rig = await EpisodeRig.StartAsync(autoReconnect: false);

        await rig.HoldAndFillAsync("HOLD/1");
        await rig.DropAsync("E1", 3);
        rig.Open("HOLD/1");
        await rig.CountReachedAsync(1 + Capacity);

        // The held event that starts the second episode is the accepted event that ends the first.
        await rig.HoldAndFillAsync("HOLD/2");
        await rig.DropAsync("E2", 2);
        rig.Open("HOLD/2");
        await rig.CountReachedAsync(2 * (1 + Capacity));
        (await rig.Peer.WriteEventAsync("VarSet", [new("Channel", "PJSIP/accepted-2"), new("Variable", "X"), new("Value", "1")])).Should().BeTrue();
        await rig.FenceAsync();

        var warnings = rig.Logger.DropLines(LogLevel.Warning);
        using (new AssertionScope())
        {
            drops.Total.Should().Be(5);
            warnings.Select(Describe).Should().Equal(
                ["first PJSIP/E1-0", "summary 3", "first PJSIP/E2-0", "summary 2"],
                "each episode logs its first refusal and its summary, in order");
        }
    }

    [Theory]
    [InlineData("caller-ending")]
    [InlineData("session-lost")]
    public async Task EventDropped_ShouldLogTheEpisodesSummaryAtTheRelease_WhenTheBufferIsReleasedDuringAnEpisode(string ending)
    {
        const int dropped = 4;
        using var drops = new BufferFullDrops();
        await using var rig = await EpisodeRig.StartAsync(autoReconnect: false);
        await rig.HoldAndFillAsync("HOLD/1");
        await rig.DropAsync("E1", dropped);

        if (ending == "session-lost")
        {
            var disconnected = rig.DisconnectedAsync();
            rig.Peer.CloseFromPeer();
            rig.Open("HOLD/1");
            await disconnected;
        }
        else
        {
            var end = rig.Connection.DisposeAsync().AsTask();
            rig.Open("HOLD/1");
            await end.WaitAsync(Bound);
        }

        await rig.Connection.DisposeAsync().AsTask().WaitAsync(Bound);

        var warnings = rig.Logger.DropLines(LogLevel.Warning);
        using (new AssertionScope())
        {
            drops.Total.Should().Be(dropped);
            warnings.Should().HaveCount(2, $"the episode open when the buffer was released ({ending}) logs its summary at the release");
            warnings.Where(w => CountOf(w) == dropped).Should().ContainSingle(
                "the summary carries the episode's count");
        }
    }

    /// <summary>The <c>Count</c> value a line carries, whatever integer type it was logged as; null when it has none.</summary>
    private static long? CountOf(LogEntry entry) =>
        entry.Values.GetValueOrDefault("Count") is IConvertible value
            ? value.ToInt64(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string Describe(LogEntry entry) =>
        entry.Format == FirstDropFormat
            ? "first " + entry.Values.GetValueOrDefault("Channel")
            : "summary " + CountOf(entry);

    private sealed record LogEntry(LogLevel Level, string? Format, string Line, IReadOnlyDictionary<string, object?> Values);

    /// <summary>Every line logged, with its level, its message template and its values.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<LogEntry> _entries = [];

        /// <summary>The lines whose template begins with the full-buffer prefix, at <paramref name="level"/>, in order.</summary>
        public List<LogEntry> DropLines(LogLevel level)
        {
            lock (_gate)
                return [.. _entries.Where(e => e.Level == level && e.Format is not null && e.Format.StartsWith(DroppedPrefix, StringComparison.Ordinal))];
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

            var format = values.TryGetValue("{OriginalFormat}", out var f) ? f as string : null;
            var line = formatter(state, exception);
            lock (_gate)
                _entries.Add(new LogEntry(logLevel, format, line, values));
        }
    }

    /// <summary>Every event dropped as a full buffer, process-wide, while this capture is alive.</summary>
    private sealed class BufferFullDrops : IDisposable
    {
        private readonly MeterListener _listener = new();
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

    /// <summary>
    /// A logged-in connection over a piped peer with an event buffer of <see cref="Capacity"/> and a capturing logger,
    /// and an <c>OnEvent</c> handler that holds the dispatch of a PeerStatus named <c>HOLD/…</c> until it is opened and
    /// counts every dispatch.
    /// </summary>
    private sealed class EpisodeRig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _peerCts = new(Bound * 6);
        private readonly Lock _sync = new();
        private readonly Dictionary<string, (TaskCompletionSource Entered, TaskCompletionSource Gate)> _holds = new(StringComparer.Ordinal);
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _count;

        public AmiConnection Connection { get; private set; } = null!;

        public PipedSocket Peer { get; private set; } = null!;

        public CapturingLogger<AmiConnection> Logger { get; } = new();

        public static async Task<EpisodeRig> StartAsync(bool autoReconnect)
        {
            var rig = new EpisodeRig();
            var factory = new PipedSocketFactory();
            rig.Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
            {
                Hostname = "localhost",
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = autoReconnect,
                DefaultEventTimeout = TimeSpan.Zero,
                EventPumpCapacity = Capacity,
            }), factory, rig.Logger);
            rig.Connection.OnEvent += rig.OnEventAsync;
            var loggedIn = Task.Run(async () =>
            {
                var peer = await factory.NextAsync(rig._peerCts.Token);
                await peer.CompleteLoginAsync(rig._peerCts.Token);
                return peer;
            }, rig._peerCts.Token);
            await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            rig.Peer = await loggedIn.WaitAsync(Bound);
            return rig;
        }

        /// <summary>The peer sends <paramref name="held"/>, whose dispatch the handler holds, then fills the buffer.</summary>
        public async Task HoldAndFillAsync(string held)
        {
            var entered = Hold(held).Entered.Task;
            (await Peer.WriteEventAsync("PeerStatus", [new("Peer", held)])).Should().BeTrue();
            await entered.WaitAsync(Bound);
            for (var i = 0; i < Capacity; i++)
                (await Peer.WriteEventAsync("PeerStatus", [new("Peer", $"FILL/{held}/{i}")])).Should().BeTrue();
            await FenceAsync();
        }

        /// <summary>The peer writes <paramref name="count"/> VarSet events on channels <c>PJSIP/{prefix}-i</c>, then a fence.</summary>
        public async Task DropAsync(string prefix, int count)
        {
            for (var i = 0; i < count; i++)
                (await Peer.WriteEventAsync("VarSet", [new("Channel", $"PJSIP/{prefix}-{i}"), new("Variable", "X"), new("Value", "1")])).Should().BeTrue();
            await FenceAsync();
        }

        public void Open(string held) => Hold(held).Gate.TrySetResult();

        /// <summary>A Ping answered by the peer: every event written before it has been read once it returns.</summary>
        public async Task FenceAsync()
        {
            var ping = Connection.SendActionAsync(new PingAction()).AsTask();
            var action = await Peer.ReadActionAsync(_peerCts.Token).WaitAsync(Bound);
            PipedSocket.IsPing(action).Should().BeTrue("the connection sends the Ping");
            (await Peer.RespondAsync("Success", PipedSocket.ActionIdOf(action!))).Should().BeTrue();
            await ping.WaitAsync(Bound);
        }

        /// <summary>Completes once the connection reads <see cref="AmiConnectionState.Disconnected"/>.</summary>
        public Task DisconnectedAsync()
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Connection.StateChanged += _ =>
            {
                if (Connection.State == AmiConnectionState.Disconnected)
                    disconnected.TrySetResult();
            };
            return disconnected.Task.WaitAsync(Bound);
        }

        /// <summary>Completes once <paramref name="count"/> dispatches have entered the handler.</summary>
        public Task CountReachedAsync(int count)
        {
            lock (_sync)
            {
                if (_count >= count)
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, signal));
                return signal.Task.WaitAsync(Bound);
            }
        }

        private (TaskCompletionSource Entered, TaskCompletionSource Gate) Hold(string peer)
        {
            lock (_sync)
            {
                if (!_holds.TryGetValue(peer, out var hold))
                {
                    hold = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    _holds[peer] = hold;
                }

                return hold;
            }
        }

        private async ValueTask OnEventAsync(ManagerEvent evt)
        {
            List<TaskCompletionSource> met = [];
            lock (_sync)
            {
                _count++;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Count <= _count)
                    {
                        met.Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var signal in met)
                signal.TrySetResult();

            var peer = evt.RawFields is not null && evt.RawFields.TryGetValue("Peer", out var p) ? p : null;
            if (peer is not null && peer.StartsWith("HOLD/", StringComparison.Ordinal))
            {
                var hold = Hold(peer);
                hold.Entered.TrySetResult();
                await hold.Gate.Task;
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                foreach (var hold in _holds.Values)
                    hold.Gate.TrySetResult();
            }

            await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
            await _peerCts.CancelAsync();
            _peerCts.Dispose();
        }
    }
}
