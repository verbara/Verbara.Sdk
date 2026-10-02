using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Binds the two halves of a call's ending that the functional tests (<c>EndingOnceTests</c>) do not
/// read: the completion measurements and span, and the saves a late leg's departure triggers.
///
/// <para>The manager ends a call when every participant has left. A leg that arrives afterwards
/// carrying the ended call's <c>linkedid</c> opens a call of its own, and its departure ends that
/// call. The ended call's ending is delivered once: the <c>CallEndedEvent</c>, the release-queue
/// entry, the <c>sessions.completed</c> count, the duration and talk-time recordings and the
/// <c>session completed</c> span all belong to it, so none of them may be produced again by the late
/// leg; the late leg's own call is measured once, as any call is. The late leg's departure is saved on
/// its own call, never on the ended one — whether the ended call is still held or already released:
/// a save after its release would hand a store a call the SDK has let go of.</para>
///
/// <para>The session instruments carry no tags and are process-wide, and this assembly runs classes in
/// parallel, so the capture counts only what is recorded on the test's own thread while it drives
/// the calls. Every step it drives is synchronous — the channel manager raises its events inline and
/// the manager ends the call inside them — so what the capture sees is exactly what this test's calls
/// produced. Retention is crossed on the manager's clock seam, never by waiting.</para>
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CallSessionManagerEndingOnceTests : IAsyncLifetime
{
    private const string ServerId = "srv-1";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly SessionOptions _options = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly RecordingStore _store = new();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sut;

    public CallSessionManagerEndingOnceTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sut = new CallSessionManager(
            Options.Create(_options), NullLogger<CallSessionManager>.Instance, _store, _clock);
        _sut.AttachToServer(_server, ServerId);
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void OnSessionCompleted_ShouldMeasureTheEndedCallOnceAndTheLateLegsOwnCallOnce_WhenALegReusingTheEndedCallsLinkedIdHangsUp()
    {
        using var capture = new CompletionCapture();

        var call = EndedCall("g");
        LegJoins("x-g", "g");
        var lateCall = _sut.GetByChannelId("x-g")?.SessionId;
        LegLeaves("x-g");

        var lateCallIsItsOwn = lateCall is not null && lateCall != call.SessionId;
        new
        {
            Endings = capture.Endings,
            DurationRecordings = capture.DurationRecordings,
            EndedCallSpans = capture.SpansOf(call.SessionId),
            LateLegOpenedItsOwnCall = lateCallIsItsOwn,
            LateCallSpans = lateCallIsItsOwn ? capture.SpansOf(lateCall!) : 0,
        }.Should().BeEquivalentTo(
            new { Endings = 2, DurationRecordings = 2, EndedCallSpans = 1, LateLegOpenedItsOwnCall = true, LateCallSpans = 1 },
            "a call that has ended cannot report another leg's ending, so a leg that arrives after it carrying its "
            + "linkedid opens a call of its own; each call ends once, so each is counted, timed and traced once");
    }

    [Fact]
    public void OnSessionCompleted_ShouldSaveTheLateLegsDepartureOnItsOwnCall_WhenTheEndedCallIsStillHeld()
    {
        var call = EndedCall("h");
        LegJoins("x-h", "h");
        var lateCall = _sut.GetByChannelId("x-h")
            ?? throw new InvalidOperationException("premise: the late leg is held in a call");
        var savesBefore = _store.SavesOf(call.SessionId);
        var lateSavesBefore = _store.SavesOf(lateCall.SessionId);

        LegLeaves("x-h");

        new
        {
            LateLegOpenedItsOwnCall = !ReferenceEquals(lateCall, call),
            EndedCallSavedAgain = _store.SavesOf(call.SessionId) - savesBefore,
            LateCallSaved = _store.SavesOf(lateCall.SessionId) - lateSavesBefore,
            LateLegLeft = lateCall.Participants.Single(p => p.UniqueId == "x-h").LeftAt.HasValue,
            EndedCallHoldsTheLateLeg = call.Participants.Any(p => p.UniqueId == "x-h"),
        }.Should().BeEquivalentTo(
            new { LateLegOpenedItsOwnCall = true, EndedCallSavedAgain = 0, LateCallSaved = 1, LateLegLeft = true, EndedCallHoldsTheLateLeg = false },
            "the late leg opened a call of its own, so its departure is recorded and saved on that call, "
            + "once, and the ended call is neither changed nor saved again");
    }

    [Fact]
    public void OnSessionCompleted_ShouldNotSaveTheCall_WhenALegReusingItsLinkedIdLeavesAfterItsRelease()
    {
        var call = EndedCall("v");
        LegJoins("x-v", "v");
        _sut.GetByChannelId("x-v").Should().NotBeNull().And.NotBeSameAs(call,
            "premise: the leg opened a call of its own while the ended call was retained");
        _clock.Advance(_options.CompletedRetention + TimeSpan.FromHours(1));
        EndedCall("n");
        _sut.GetById(call.SessionId).Should().BeNull(
            "premise: the next call's ending released the call while the late leg was still up");
        var savesBefore = _store.SavesOf(call.SessionId);

        LegLeaves("x-v");

        (_store.SavesOf(call.SessionId) - savesBefore).Should().Be(0,
            "the manager has released the call, so the departure of a leg that outlived it is not "
            + "saved: a save now would hand the store a call the SDK has already let go of");
    }

    /// <summary>Two legs sharing <c>L-{tag}</c>, dialled, answered and both hung up: one ended call.</summary>
    private CallSession EndedCall(string tag)
    {
        var linkedId = $"L-{tag}";
        _server.Channels.OnNewChannel($"c-{tag}", $"PJSIP/trunk-c-{tag}", ChannelState.Ring,
            context: "from-trunk", linkedId: linkedId);
        _server.Channels.OnNewChannel($"a-{tag}", $"PJSIP/100-a-{tag}", ChannelState.Ring, linkedId: linkedId);
        _server.Channels.OnDialBegin($"c-{tag}", $"a-{tag}", $"PJSIP/100-a-{tag}", null);
        _server.Channels.OnNewState($"a-{tag}", ChannelState.Up);
        var session = _sut.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"premise: a session was opened for '{linkedId}'");

        _server.Channels.OnHangup($"a-{tag}", HangupCause.NormalClearing);
        _server.Channels.OnHangup($"c-{tag}", HangupCause.NormalClearing);
        session.State.Should().Be(CallSessionState.Completed, $"premise: '{tag}' ended with both legs hung up");
        return session;
    }

    private void LegJoins(string uniqueId, string tag) =>
        _server.Channels.OnNewChannel(uniqueId, $"PJSIP/300-{uniqueId}", ChannelState.Ring, linkedId: $"L-{tag}");

    private void LegLeaves(string uniqueId) =>
        _server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing);


    /// <summary>
    /// What the ending of a call records on the session meter and activity source, counted only on the
    /// thread that created the capture: the instruments are process-wide and untagged, and other test
    /// classes run in parallel on other threads.
    /// </summary>
    private sealed class CompletionCapture : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly MeterListener _meters = new();
        private readonly ActivityListener _spans;
        private readonly ConcurrentDictionary<string, int> _measurements = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _stoppedSpans = new();

        public CompletionCapture()
        {
            // Read both before either listener is registered. Registering an activity listener asks every
            // existing source whether to listen; a source first created from inside that question is
            // created too late to be asked, and the capture then sees no span when it runs first.
            var meter = SessionMetrics.Meter;
            var source = SessionActivitySource.Source;

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>((instrument, value, _, _) => Count(instrument, (int)value));
            _meters.SetMeasurementEventCallback<double>((instrument, _, _, _) => Count(instrument, 1));
            _meters.Start();

            _spans = new ActivityListener
            {
                ShouldListenTo = candidate => ReferenceEquals(candidate, source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (OnCapturingThread)
                        _stoppedSpans.Enqueue(activity.OperationName);
                },
            };
            ActivitySource.AddActivityListener(_spans);
        }

        private bool OnCapturingThread => Environment.CurrentManagedThreadId == _thread;

        /// <summary>Calls counted as ended, whatever their final state: completed, failed or timed out.</summary>
        public int Endings =>
            _measurements.GetValueOrDefault("sessions.completed")
            + _measurements.GetValueOrDefault("sessions.failed")
            + _measurements.GetValueOrDefault("sessions.timed_out");

        public int DurationRecordings => _measurements.GetValueOrDefault("sessions.duration");

        public int SpansOf(string sessionId) =>
            _stoppedSpans.Count(name => name == $"session completed {sessionId}");

        public void Dispose()
        {
            _spans.Dispose();
            _meters.Dispose();
        }

        private void Count(Instrument instrument, int by)
        {
            if (OnCapturingThread)
                _measurements.AddOrUpdate(instrument.Name, by, (_, sum) => sum + by);
        }
    }

    /// <summary>A store that keeps nothing and counts each save per session.</summary>
    private sealed class RecordingStore : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, int> _saves = new(StringComparer.Ordinal);

        public int SavesOf(string sessionId) => _saves.GetValueOrDefault(sessionId);

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _saves.AddOrUpdate(session.SessionId, 1, (_, n) => n + 1);
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }

    /// <summary>The manager's release clock; moves only when the test moves it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _now += by;
            }
        }
    }
}
