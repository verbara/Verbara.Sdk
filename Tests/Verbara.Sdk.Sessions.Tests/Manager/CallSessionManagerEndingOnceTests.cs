using System.Collections.Concurrent;
using System.Diagnostics;
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
/// read: the completion measurements and span, and the save a repeat departure triggers.
///
/// <para>The manager ends a call when every participant has left, and reaches that point again when
/// a leg that joined the ended call — carrying its <c>linkedid</c> — leaves too. The ending is
/// delivered once: the <c>CallEndedEvent</c>, the release-queue entry, the <c>sessions.completed</c>
/// count, the duration and talk-time recordings and the <c>session completed</c> span all belong to
/// it, so none of them may be produced again by the repeat. What the repeat still does is save the
/// call, which now records the late leg as joined and left — but only while the manager still holds
/// the call: a save after its release would hand a store a call the SDK has let go of.</para>
///
/// <para>The session instruments carry no tags and are process-wide, and this assembly runs classes in
/// parallel, so the capture counts only what is recorded on the test's own thread while it drives
/// the calls. Every step it drives is synchronous — the channel manager raises its events inline and
/// the manager ends the call inside them — so what the capture sees is exactly what this test's calls
/// produced. Retention is crossed on the manager's clock seam, never by waiting.</para>
/// </summary>
public sealed class CallSessionManagerEndingOnceTests : IAsyncDisposable
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

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void OnSessionCompleted_ShouldRecordTheCompletionMeasurementsAndSpanOnce_WhenALegReusingTheEndedCallsLinkedIdHangsUp()
    {
        using var capture = new CompletionCapture();

        var call = EndedCall("g");
        LegJoins("x-g", "g");
        (_sut.GetByChannelId("x-g")?.SessionId).Should().Be(call.SessionId,
            "premise: a leg carrying an ended call's linkedid joins that call while it is retained");
        LegLeaves("x-g");

        capture.For(call.SessionId).Should().BeEquivalentTo(
            new Completion(SessionsCompleted: 1, DurationRecordings: 1, TalkTimeRecordings: 1, Spans: 1),
            "the call ended once, so it is counted, timed and traced once; a leg that joined it after "
            + "its ending and left does not complete it again");
    }

    [Fact]
    public void OnSessionCompleted_ShouldSaveTheLateLegsDeparture_WhenTheEndedCallIsStillHeld()
    {
        var call = EndedCall("h");
        LegJoins("x-h", "h");
        var savesBefore = _store.SavesOf(call.SessionId);

        LegLeaves("x-h");

        new
        {
            SavedAgain = _store.SavesOf(call.SessionId) - savesBefore,
            LateLegLeft = call.Participants.Single(p => p.UniqueId == "x-h").LeftAt.HasValue,
        }.Should().BeEquivalentTo(
            new { SavedAgain = 1, LateLegLeft = true },
            "the late leg's departure delivers no ending, but it is recorded on the call, and while the "
            + "manager still holds the call that record is saved like any other change to it");
    }

    [Fact]
    public void OnSessionCompleted_ShouldNotSaveTheCall_WhenALegThatJoinedItLeavesAfterItsRelease()
    {
        var call = EndedCall("v");
        LegJoins("x-v", "v");
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

    private sealed record Completion(int SessionsCompleted, int DurationRecordings, int TalkTimeRecordings, int Spans);

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
            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, SessionMetrics.Meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>((instrument, value, _, _) => Count(instrument, (int)value));
            _meters.SetMeasurementEventCallback<double>((instrument, _, _, _) => Count(instrument, 1));
            _meters.Start();

            _spans = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, SessionActivitySource.Source),
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

        public Completion For(string sessionId) => new(
            SessionsCompleted: _measurements.GetValueOrDefault("sessions.completed"),
            DurationRecordings: _measurements.GetValueOrDefault("sessions.duration"),
            TalkTimeRecordings: _measurements.GetValueOrDefault("sessions.talk_time"),
            Spans: _stoppedSpans.Count(name => name == $"session completed {sessionId}"));

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
