using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// Replays a byte-exact AMI capture from <c>Recordings/asterisk-ami/</c> through the path a live
/// connection takes: <see cref="AmiProtocolReader"/> → <c>GeneratedEventDeserializer</c> →
/// <see cref="VerbaraServer"/>'s own event observer → <see cref="CallSessionManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// The server is started against a substitute <see cref="IAmiConnection"/> whose state-load actions
/// return nothing, and every event is delivered to the observer the server subscribed in
/// <see cref="VerbaraServer.StartAsync"/> — the production observer, not a copy of its switch. An
/// exception that observer throws is caught and recorded, and the replay moves on to the next event.
/// That is what <c>AmiConnection.DispatchEventAsync</c> does with it ("Observer errors should not
/// crash the pump"), so a replay reports both what a consumer observes and what the dispatcher
/// swallows.
/// </para>
/// <para>
/// Two families of capture are replayed, and each attributes calls its own way (the folder's README
/// describes both). The twelve call shapes (<see cref="ReplayAsync"/>) carry marker frames
/// (<c>UserEvent: N5Marker</c> with <c>Scenario: &lt;id&gt;</c>): a call belongs to the scenario whose
/// marker was the last one delivered before its <see cref="CallStartedEvent"/>. The seventeen queue
/// shapes (<see cref="ReplayQueueShapesAsync"/>) carry no marker; each is placed by its own caller
/// number (<see cref="QueueShapes"/>).
/// </para>
/// <para>
/// Delivery is synchronous and in capture order, and every session transition runs on the delivering
/// thread, so the result is complete when the replay returns. Nothing waits.
/// </para>
/// </remarks>
internal static class AmiCaptureReplay
{
    /// <summary>
    /// The twelve-shape captures, one per Asterisk version (20.20.1, 22.9.0, 23.4.1). Each runs the
    /// same twelve scenarios, <c>S1</c> to <c>S12</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> CallShapeCaptures =
    [
        "call-shapes-asterisk-20.20.1.raw",
        "call-shapes-asterisk-22.9.0.raw",
        "call-shapes-asterisk-23.4.1.raw",
    ];

    /// <summary>
    /// The queue-shape captures, one per Asterisk version (20.20.1, 22.9.0, 23.4.1). Each runs the
    /// same fifteen queue shapes and two calls that never join a queue (<see cref="QueueShapes.All"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> QueueShapeCaptures =
    [
        "queue-shapes-asterisk-20.20.1.raw",
        "queue-shapes-asterisk-22.9.0.raw",
        "queue-shapes-asterisk-23.4.1.raw",
    ];

    /// <summary>
    /// The queue-reload captures, one per Asterisk version (20.20.1, 22.9.0, 23.4.1). Each places the same
    /// four calls into <c>q-late</c>, one after the other, and holds the snapshots the tap took of each
    /// (<see cref="CapturedSnapshot"/>, the folder's README, <i>The reload captures</i>).
    /// </summary>
    public static readonly IReadOnlyList<string> QueueReloadCaptures =
    [
        "queue-reload-asterisk-20.20.1.raw",
        "queue-reload-asterisk-22.9.0.raw",
        "queue-reload-asterisk-23.4.1.raw",
    ];

    private const string MarkerUserEvent = "N5Marker";
    private const string BeforeFirstMarker = "(before-the-first-marker)";

    /// <summary>
    /// Replays <paramref name="fixture"/> (a file name in <c>Recordings/asterisk-ami/</c>) into a
    /// fresh server and session manager, with the default in-memory session store.
    /// </summary>
    public static async Task<CaptureReplay> ReplayAsync(string fixture, SessionOptions? options = null)
    {
        await using var rig = await ReplayRig.StartAsync(options ?? new SessionOptions());
        var manager = rig.Manager;

        var scenario = BeforeFirstMarker;
        var calls = new List<ReplayedCall>();
        var callsBySessionId = new Dictionary<string, ReplayedCall>(StringComparer.Ordinal);
        var swallowed = new List<SwallowedObserverException>();

        using var domainEvents = manager.Events.Subscribe(evt =>
        {
            if (evt is CallStartedEvent)
            {
                var session = manager.GetById(evt.SessionId)
                    ?? throw new InvalidOperationException(
                        $"CallStartedEvent for {evt.SessionId}, which the manager does not hold.");
                var call = new ReplayedCall(scenario, session);
                calls.Add(call);
                callsBySessionId[evt.SessionId] = call;
            }

            if (callsBySessionId.TryGetValue(evt.SessionId, out var target))
                target.Record(evt);
        });

        await foreach (var evt in ReadEventsAsync(FixturePath(fixture)))
        {
            if (TryReadMarker(evt, out var next))
                scenario = next;

            if (rig.Deliver(evt) is { } thrown)
                swallowed.Add(new SwallowedObserverException(scenario, evt.EventType ?? "", thrown));
        }

        return new CaptureReplay(fixture, calls, swallowed);
    }

    /// <summary>
    /// Replays the queue-shape capture <paramref name="fixture"/> (a file name in
    /// <c>Recordings/asterisk-ami/</c>) into a fresh server, session manager and
    /// <see cref="QueueSessionTracker"/>, and scores every shape against Asterisk's own verdict in the
    /// same capture (<see cref="QueueShapeScore"/>).
    /// </summary>
    /// <param name="fixture">The capture's file name.</param>
    /// <param name="dispatch">
    /// Optional. The replay keeps it current while each frame is delivered, so a caller can attribute a
    /// measurement taken inside the delivery to the shape of the frame being dispatched.
    /// </param>
    public static Task<QueueShapeReplay> ReplayQueueShapesAsync(string fixture, QueueShapeDispatch? dispatch = null) =>
        ReplayQueueShapesFromFileAsync(FixturePath(fixture), dispatch);

    /// <summary>
    /// <see cref="ReplayQueueShapesAsync"/> over any file with the same shapes, such as a capture kept
    /// outside the suite's <c>Recordings/</c> folder, to compare it with the fixture reduced from it.
    /// </summary>
    public static async Task<QueueShapeReplay> ReplayQueueShapesFromFileAsync(string path, QueueShapeDispatch? dispatch = null)
    {
        var options = new SessionOptions();
        await using var rig = await ReplayRig.StartAsync(options);
        var manager = rig.Manager;
        using var tracker = new QueueSessionTracker(manager, Options.Create(options));

        var scores = QueueShapes.All.ToDictionary(s => s.Id, s => new QueueShapeScore(s), StringComparer.Ordinal);
        var shapeBySessionId = new Dictionary<string, QueueShape>(StringComparer.Ordinal);
        var trackerSeen = new Dictionary<string, QueueCounters>(StringComparer.Ordinal);
        var unattributed = new QueueCounters(0, 0, 0, 0);

        // Subscribed after the tracker, so the tracker has handled each domain event by the time it
        // arrives here, and the change read from the tracker is that event's change.
        using var scoring = manager.Events.Subscribe(evt =>
        {
            if (evt is CallStartedEvent started
                && QueueShapes.Of(started.CallerIdNum, FirstChannel(manager.GetById(started.SessionId))) is { } startedShape
                && shapeBySessionId.TryAdd(started.SessionId, startedShape)
                && manager.GetById(started.SessionId) is { } startedSession)
            {
                scores[startedShape.Id].RecordSession(startedSession);
            }

            var score = shapeBySessionId.TryGetValue(evt.SessionId, out var shape) ? scores[shape.Id] : null;
            if (evt is CallConnectedEvent connected)
            {
                score?.RecordConnected(connected);
                dispatch?.OnConnected(shape?.Id);
            }

            var change = TakeTrackerChange(tracker, trackerSeen);
            if (score is not null)
                score.RecordTrackerChange(change);
            else
                unattributed += change;
        });

        var shapeByUniqueId = new Dictionary<string, QueueShape>(StringComparer.Ordinal);
        var shapeByLinkedId = new Dictionary<string, QueueShape>(StringComparer.Ordinal);
        var swallowed = new List<SwallowedObserverException>();

        await foreach (var evt in ReadEventsAsync(path))
        {
            var frameShape = ShapeOfFrame(evt, shapeByUniqueId, shapeByLinkedId, out var callerShape);
            if (callerShape is not null)
            {
                // Asterisk's own verdict, on the queue caller's channel: app_queue's AgentConnect is an
                // answered visit and its QueueCallerAbandon an abandoned one.
                if (string.Equals(evt.EventType, "AgentConnect", StringComparison.OrdinalIgnoreCase))
                    scores[callerShape.Id].AsteriskConnects++;
                else if (string.Equals(evt.EventType, "QueueCallerAbandon", StringComparison.OrdinalIgnoreCase))
                    scores[callerShape.Id].AsteriskAbandons++;
            }

            dispatch?.Begin(frameShape?.Id, evt.EventType);
            var thrown = rig.Deliver(evt);
            dispatch?.End();

            if (thrown is not null)
            {
                swallowed.Add(new SwallowedObserverException(frameShape?.Id ?? QueueShapeDispatch.Unattributed, evt.EventType ?? "", thrown));
                if (frameShape is not null)
                    scores[frameShape.Id].ObserverExceptions++;
            }
        }

        return new QueueShapeReplay(Path.GetFileName(path), [.. QueueShapes.All.Select(s => scores[s.Id])], unattributed, swallowed);
    }

    /// <summary>
    /// The shape a frame belongs to: the shape of the call whose <c>Newchannel</c> introduced its
    /// <c>Uniqueid</c>, or failing that the shape whose caller's <c>Newchannel</c> carried its
    /// <c>Linkedid</c> (a member's leg). <paramref name="callerShape"/> is set only for the first case,
    /// a frame on a shape's own caller channel.
    /// </summary>
    private static QueueShape? ShapeOfFrame(
        ManagerEvent evt,
        Dictionary<string, QueueShape> shapeByUniqueId,
        Dictionary<string, QueueShape> shapeByLinkedId,
        out QueueShape? callerShape)
    {
        callerShape = null;
        if (evt.RawFields is not { } fields || !fields.TryGetValue("Uniqueid", out var uniqueId))
            return null;

        if (string.Equals(evt.EventType, "Newchannel", StringComparison.OrdinalIgnoreCase)
            && QueueShapes.Of(fields.GetValueOrDefault("CallerIDNum"), fields.GetValueOrDefault("Channel")) is { } introduced)
        {
            shapeByUniqueId.TryAdd(uniqueId, introduced);
            if (fields.TryGetValue("Linkedid", out var introducedLinkedId))
                shapeByLinkedId.TryAdd(introducedLinkedId, introduced);
        }

        if (shapeByUniqueId.TryGetValue(uniqueId, out var own))
        {
            callerShape = own;
            return own;
        }

        return fields.TryGetValue("Linkedid", out var linkedId) ? shapeByLinkedId.GetValueOrDefault(linkedId) : null;
    }

    private static string? FirstChannel(CallSession? session) =>
        session?.Participants is [var first, ..] ? first.Channel : null;

    /// <summary>What every queue's counters moved by since the last call, summed over the queues.</summary>
    private static QueueCounters TakeTrackerChange(QueueSessionTracker tracker, Dictionary<string, QueueCounters> seen)
    {
        var change = new QueueCounters(0, 0, 0, 0);
        foreach (var queue in tracker.ActiveQueues)
        {
            var now = new QueueCounters(queue.CallsOffered, queue.CallsAnswered, queue.CallsAbandoned, queue.CallsWaiting);
            change += now - seen.GetValueOrDefault(queue.QueueName, new QueueCounters(0, 0, 0, 0));
            seen[queue.QueueName] = now;
        }

        return change;
    }

    /// <summary>
    /// Replays one call of the queue-reload capture <paramref name="fixture"/> into a fresh server, session
    /// manager and <see cref="QueueSessionTracker"/>, optionally withholding part of the call's live frames and
    /// running a reconnect reload answered with one of the snapshots the capture holds.
    /// </summary>
    /// <param name="fixture">The capture's file name, one of <see cref="QueueReloadCaptures"/>.</param>
    /// <param name="callerNumber">
    /// The call to replay, by its caller number. Only its frames are replayed: from its caller's
    /// <c>Newchannel</c> up to the next call's first <c>Newchannel</c>. The captures place their calls one
    /// after the other, and the replay throws if a frame of this call lies outside that range.
    /// </param>
    /// <param name="reloadWith">
    /// The snapshot to answer the reload with, or <c>null</c> for no reload. The reload runs where that
    /// snapshot sits in the capture: at its <c>UserEvent</c> marker, whose own <c>Timestamp</c> is the reload
    /// instant. The last live frame before the marker cannot stand in for it: a caller waiting in a queue
    /// emits nothing, so that frame can be seconds older.
    /// </param>
    /// <param name="outage">
    /// The call's live frames to withhold, from where the outage starts up to the reload; <c>null</c> for
    /// none. It needs a reload to end at.
    /// </param>
    /// <param name="clock">
    /// The manager's clock seam. The replay sets it from each live frame's <c>Timestamp</c> before
    /// delivering the frame, and to the marker's before the reload, so every instant the manager reads is
    /// one Asterisk stamped.
    /// </param>
    /// <remarks>
    /// Only live frames are delivered to the server's observer: every frame that carries an
    /// <c>ActionID</c> is the tap's own marker or an answer to the tap's own actions. At the reload, the
    /// substitute connection reconnects, and the server's <c>Status</c> and <c>QueueStatus</c> are answered
    /// with the snapshot's captured frames, parsed by the production reader, and <c>Agents</c> with nothing
    /// (<see cref="ReloadableConnection"/>). An exception the observer throws is caught and recorded, as the
    /// dispatcher does.
    /// </remarks>
    public static async Task<QueueReloadReplay> ReplayQueueReloadAsync(string fixture, string callerNumber,
        CapturedSnapshot? reloadWith, Outage? outage, ManualClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (outage is not null && reloadWith is null)
            throw new ArgumentException("An outage ends at a reload; give the snapshot to reload with.", nameof(outage));

        var frames = await ReadCaptureAsync(fixture);
        var call = CallWindow.Find(fixture, frames, callerNumber);

        var markerIndex = reloadWith is null ? -1 : call.MarkerOf(reloadWith);
        var withheldFrom = outage is null ? -1 : call.OutageStart(outage);
        if (outage is not null && withheldFrom > markerIndex)
            throw new ArgumentException($"{fixture}: the outage starts after the snapshot it ends at.", nameof(outage));

        var status = reloadWith is null ? [] : frames.Where(f => HasActionId(f, reloadWith.StatusActionId)).ToList();
        var queueStatus = reloadWith is null ? [] : frames.Where(f => HasActionId(f, reloadWith.QueueStatusActionId)).ToList();
        if (reloadWith is not null && (status.Count == 0 || queueStatus.Count == 0))
            throw new ArgumentException($"{fixture}: no answer carries the ActionIDs of {reloadWith}.", nameof(reloadWith));

        var options = new SessionOptions();
        var connection = new ReloadableConnection();
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(options), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore(), clock);
        try
        {
            using var tracker = new QueueSessionTracker(manager, Options.Create(options));
            manager.AttachToServer(server, "reload-replay");
            await server.StartAsync();

            var sessions = new List<CallSession>();
            var queued = new List<CallQueuedEvent>();
            var connected = new List<CallConnectedEvent>();
            using var recording = manager.Events.Subscribe(evt =>
            {
                switch (evt)
                {
                    case CallStartedEvent started when manager.GetById(started.SessionId) is { } session:
                        sessions.Add(session);
                        break;
                    case CallQueuedEvent q:
                        queued.Add(q);
                        break;
                    case CallConnectedEvent c:
                        connected.Add(c);
                        break;
                }
            });

            var dispatch = new QueueShapeDispatch();
            using var samples = new WaitTimeSamples(dispatch);
            var swallowed = new List<SwallowedObserverException>();
            DateTimeOffset? reloadInstant = null;

            for (var i = call.First; i < call.End; i++)
            {
                var frame = frames[i];
                if (i == markerIndex)
                {
                    reloadInstant = InstantOf(frame);
                    clock.MoveTo(reloadInstant.Value);
                    await connection.ReloadAsync(status, queueStatus);
                    continue;
                }

                if (frame.RawFields?.ContainsKey("ActionID") == true)
                    continue;
                if (withheldFrom >= 0 && i >= withheldFrom && i < markerIndex)
                    continue;

                clock.MoveTo(InstantOf(frame));
                dispatch.Begin(callerNumber, frame.EventType);
                Exception? thrown;
                try
                {
                    connection.Observer.OnNext(frame);
                    thrown = null;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // AmiConnection.DispatchEventAsync catches what an observer throws and carries on, and
                    // so does a replay; the exception is reported with the result. Running out of memory is
                    // not the observer's fault, so it ends the replay instead.
                    thrown = ex;
                }
                finally
                {
                    dispatch.End();
                }

                if (thrown is not null)
                    swallowed.Add(new SwallowedObserverException(callerNumber, frame.EventType ?? "", thrown));
            }

            var counts = tracker.ActiveQueues.ToDictionary(
                q => q.QueueName,
                q => new QueueCounters(q.CallsOffered, q.CallsAnswered, q.CallsAbandoned, q.CallsWaiting),
                StringComparer.Ordinal);
            var recordedWait = tracker.ActiveQueues.ToDictionary(q => q.QueueName, q => q.TotalWaitTime, StringComparer.Ordinal);
            var entry = queueStatus.OfType<QueueEntryEvent>()
                .SingleOrDefault(e => string.Equals(e.Uniqueid, call.CallerUniqueId, StringComparison.Ordinal));

            return new QueueReloadReplay(fixture, callerNumber, counts, recordedWait, queued, connected,
                [.. samples.All.Where(s => s.Shape == callerNumber).Select(s => s.Milliseconds)],
                sessions, reloadInstant, entry, call.CallerQueueFrames(withheldFrom, markerIndex), swallowed);
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// The events of <paramref name="fixture"/> (a file name in <c>Recordings/asterisk-ami/</c>), in order,
    /// parsed as a live connection parses them: live events, markers and the answers to the tap's actions
    /// alike.
    /// </summary>
    public static Task<IReadOnlyList<ManagerEvent>> ReadCaptureAsync(string fixture) =>
        ReadCaptureAsync(File.ReadAllBytes(FixturePath(fixture)));

    /// <summary>The events of a capture held in memory, parsed as <see cref="ReadCaptureAsync(string)"/> parses a file.</summary>
    public static async Task<IReadOnlyList<ManagerEvent>> ReadCaptureAsync(byte[] capture)
    {
        var events = new List<ManagerEvent>();
        await using var stream = new MemoryStream(capture, writable: false);
        await foreach (var evt in ReadEventsAsync(stream))
            events.Add(evt);
        return events;
    }

    /// <summary>The bytes of <paramref name="fixture"/>, for a test that alters a copy in memory.</summary>
    public static byte[] ReadCaptureBytes(string fixture) => File.ReadAllBytes(FixturePath(fixture));

    /// <summary>
    /// The instant Asterisk stamped on <paramref name="evt"/>: its <c>Timestamp</c> header, read as the exact
    /// decimal it is (a <see cref="double"/> would round away the microseconds).
    /// </summary>
    public static DateTimeOffset InstantOf(ManagerEvent evt) =>
        evt.RawFields?.GetValueOrDefault("Timestamp") is { } stamp
            ? DateTimeOffset.UnixEpoch.AddTicks(
                (long)(decimal.Parse(stamp, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond))
            : throw new InvalidOperationException(
                $"A {evt.EventType} frame carries no Timestamp: the capture was not taken with timestampevents = yes.");

    private static bool HasActionId(ManagerEvent evt, string actionId) =>
        evt.RawFields?.GetValueOrDefault("ActionID") is { } id && string.Equals(id, actionId, StringComparison.Ordinal);

    /// <summary>One call's frames within a queue-reload capture, and where its snapshots and queue frames sit.</summary>
    private sealed class CallWindow
    {
        private readonly string _fixture;
        private readonly IReadOnlyList<ManagerEvent> _frames;

        private CallWindow(string fixture, IReadOnlyList<ManagerEvent> frames, int first, int end, string callerUniqueId)
        {
            _fixture = fixture;
            _frames = frames;
            First = first;
            End = end;
            CallerUniqueId = callerUniqueId;
        }

        /// <summary>The index of the call's first frame, its caller's <c>Newchannel</c>.</summary>
        public int First { get; }

        /// <summary>The index just past the call's last frame.</summary>
        public int End { get; }

        public string CallerUniqueId { get; }

        public static CallWindow Find(string fixture, IReadOnlyList<ManagerEvent> frames, string callerNumber)
        {
            var first = -1;
            for (var i = 0; i < frames.Count && first < 0; i++)
            {
                if (StartsACall(frames[i])
                    && string.Equals(frames[i].RawFields?.GetValueOrDefault("CallerIDNum"), callerNumber, StringComparison.Ordinal))
                {
                    first = i;
                }
            }

            if (first < 0)
                throw new ArgumentException($"{fixture}: no call from {callerNumber}.", nameof(callerNumber));

            var end = first + 1;
            while (end < frames.Count && !StartsACall(frames[end]))
                end++;

            var callerUniqueId = frames[first].UniqueId ?? "";
            for (var i = 0; i < frames.Count; i++)
            {
                if ((i < first || i >= end)
                    && string.Equals(frames[i].RawFields?.GetValueOrDefault("Linkedid"), callerUniqueId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{fixture}: frame {i} ({frames[i].EventType}) belongs to the call from {callerNumber} but lies "
                        + "outside its range; the capture's calls overlap.");
                }
            }

            return new CallWindow(fixture, frames, first, end, callerUniqueId);
        }

        /// <summary>The index of the <c>UserEvent</c> marker the tap sent right before <paramref name="snapshot"/>.</summary>
        public int MarkerOf(CapturedSnapshot snapshot)
        {
            var answer = -1;
            for (var i = First; i < End && answer < 0; i++)
            {
                if (HasActionId(_frames[i], snapshot.StatusActionId))
                    answer = i;
            }

            for (var i = answer - 1; i >= First; i--)
            {
                if (string.Equals(_frames[i].EventType, "UserEvent", StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            throw new ArgumentException($"{_fixture}: {snapshot} is not a snapshot of this call, or has no marker.", nameof(snapshot));
        }

        /// <summary>The index of the first live frame <paramref name="outage"/> withholds.</summary>
        public int OutageStart(Outage outage)
        {
            if (outage.AfterCallerEvent is null)
                return First;

            var seen = 0;
            for (var i = First; i < End; i++)
            {
                if (IsOnCaller(_frames[i], outage.AfterCallerEvent) && ++seen == outage.Ordinal)
                    return i + 1;
            }

            throw new ArgumentException($"{_fixture}: the caller has no {outage.AfterCallerEvent} #{outage.Ordinal}.", nameof(outage));
        }

        /// <summary>
        /// app_queue's frames on the caller's channel, in order, withheld or not: its joins, leaves, abandons
        /// and connections, each with the instant Asterisk stamped on it.
        /// </summary>
        public List<CapturedQueueFrame> CallerQueueFrames(int withheldFrom, int markerIndex)
        {
            var found = new List<CapturedQueueFrame>();
            for (var i = First; i < End; i++)
            {
                var frame = _frames[i];
                if (frame.RawFields is not { } fields
                    || fields.ContainsKey("ActionID")
                    || !string.Equals(fields.GetValueOrDefault("Uniqueid"), CallerUniqueId, StringComparison.Ordinal)
                    || frame.EventType is not ("QueueCallerJoin" or "QueueCallerLeave" or "QueueCallerAbandon" or "AgentConnect"))
                {
                    continue;
                }

                var holdTime = fields.GetValueOrDefault("HoldTime") is { } h
                    ? long.Parse(h, NumberStyles.None, CultureInfo.InvariantCulture)
                    : (long?)null;
                var delivered = !(withheldFrom >= 0 && i >= withheldFrom && i < markerIndex);
                found.Add(new CapturedQueueFrame(frame.EventType, fields.GetValueOrDefault("Queue") ?? "", InstantOf(frame), holdTime, delivered));
            }

            return found;
        }

        private bool IsOnCaller(ManagerEvent frame, string eventType) =>
            string.Equals(frame.EventType, eventType, StringComparison.OrdinalIgnoreCase)
            && !(frame.RawFields?.ContainsKey("ActionID") ?? false)
            && string.Equals(frame.RawFields?.GetValueOrDefault("Uniqueid"), CallerUniqueId, StringComparison.Ordinal);

        /// <summary>A call's first channel: a live <c>Newchannel</c> whose <c>Linkedid</c> is its own <c>Uniqueid</c>.</summary>
        private static bool StartsACall(ManagerEvent frame) =>
            string.Equals(frame.EventType, "Newchannel", StringComparison.OrdinalIgnoreCase)
            && frame.RawFields is { } fields
            && !fields.ContainsKey("ActionID")
            && fields.GetValueOrDefault("Uniqueid") is { } uniqueId
            && string.Equals(fields.GetValueOrDefault("Linkedid"), uniqueId, StringComparison.Ordinal);
    }

    private static string FixturePath(string fixture)
    {
        var path = Path.Join(AppContext.BaseDirectory, "Recordings", "asterisk-ami", fixture);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"Capture '{fixture}' is not in the test output. It is copied there from the suite's "
                + "Recordings/asterisk-ami/ folder by the project file.", path);
    }

    /// <summary>The capture's events, in order, parsed as a live connection parses them.</summary>
    private static async IAsyncEnumerable<ManagerEvent> ReadEventsAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        await foreach (var evt in ReadEventsAsync(stream))
            yield return evt;
    }

    /// <summary>The events of <paramref name="stream"/>, in order, parsed as a live connection parses them.</summary>
    private static async IAsyncEnumerable<ManagerEvent> ReadEventsAsync(Stream stream)
    {
        var reader = new AmiProtocolReader(PipeReader.Create(stream));
        while (await reader.ReadMessageAsync() is { } message)
        {
            // Responses go to the action that is waiting for them and the banner to the login, never
            // to an observer. Every event goes to the observers, including those that also complete
            // an event-generating action (AmiConnection.ReaderLoopAsync).
            if (!message.IsEvent)
                continue;

            yield return GeneratedEventDeserializer.Deserialize(message);
        }
    }

    private static bool TryReadMarker(ManagerEvent evt, out string scenario)
    {
        scenario = "";
        if (!string.Equals(evt.EventType, "UserEvent", StringComparison.Ordinal) || evt.RawFields is not { } fields)
            return false;
        if (!fields.TryGetValue("UserEvent", out var name) || !string.Equals(name, MarkerUserEvent, StringComparison.Ordinal))
            return false;
        if (!fields.TryGetValue("Scenario", out var id))
            return false;

        scenario = id;
        return true;
    }

    /// <summary>
    /// A started server and a session manager attached to it, with the server's own event observer
    /// taken from the substitute connection it subscribed to.
    /// </summary>
    private sealed class ReplayRig : IAsyncDisposable
    {
        private readonly VerbaraServer _server;
        private readonly IObserver<ManagerEvent> _observer;

        private ReplayRig(VerbaraServer server, CallSessionManager manager, IObserver<ManagerEvent> observer)
        {
            _server = server;
            Manager = manager;
            _observer = observer;
        }

        public CallSessionManager Manager { get; }

        public static async Task<ReplayRig> StartAsync(SessionOptions options)
        {
            var connection = Substitute.For<IAmiConnection>();
            connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
                .Returns(_ => NoEvents());
            IObserver<ManagerEvent>? observer = null;
            connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => observer = o))
                .Returns(Substitute.For<IDisposable>());

            var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
            var manager = new CallSessionManager(
                Options.Create(options),
                NullLogger<CallSessionManager>.Instance,
                new InMemorySessionStore());
            try
            {
                manager.AttachToServer(server, "replay");
                await server.StartAsync();
                return new ReplayRig(server, manager, observer
                    ?? throw new InvalidOperationException("VerbaraServer.StartAsync subscribed no event observer."));
            }
            catch
            {
                await manager.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Delivers <paramref name="evt"/> to the server's observer and returns what it threw, if
        /// anything: <c>AmiConnection.DispatchEventAsync</c> catches everything an observer throws and
        /// carries on, and so does a replay.
        /// </summary>
        public Exception? Deliver(ManagerEvent evt)
        {
            try
            {
                _observer.OnNext(evt);
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return ex;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            await _server.DisposeAsync();
        }

        private static async IAsyncEnumerable<ManagerEvent> NoEvents()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

/// <summary>What one replay produced: every call it opened, and every exception the dispatcher swallowed.</summary>
internal sealed class CaptureReplay(
    string fixture,
    IReadOnlyList<ReplayedCall> calls,
    IReadOnlyList<SwallowedObserverException> observerExceptions)
{
    public string Fixture { get; } = fixture;

    /// <summary>Every call the replay opened, in the order it started.</summary>
    public IReadOnlyList<ReplayedCall> Calls { get; } = calls;

    /// <summary>Every exception the server's observer threw, in delivery order.</summary>
    public IReadOnlyList<SwallowedObserverException> ObserverExceptions { get; } = observerExceptions;

    /// <summary>
    /// The one call scenario <paramref name="scenarioId"/> (<c>"S1"</c> … <c>"S12"</c>) produced.
    /// Throws when the scenario produced none or several, since every pin assumes one call each.
    /// </summary>
    public ReplayedCall Call(string scenarioId)
    {
        var matches = Calls.Where(c => string.Equals(c.ScenarioId, scenarioId, StringComparison.Ordinal)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"{Fixture}: scenario {scenarioId} produced {matches.Count} calls, expected 1.{Environment.NewLine}{Describe()}");
    }

    /// <summary>One line per call, for assertion messages.</summary>
    public string Describe() =>
        $"{Fixture}:{Environment.NewLine}" + string.Join(Environment.NewLine, Calls.Select(c => c.ToString()));
}

/// <summary>A call a replay opened: the scenario it belongs to, its session, and its domain events.</summary>
internal sealed class ReplayedCall(string scenario, CallSession session)
{
    private readonly List<SessionDomainEvent> _domainEvents = [];

    /// <summary>The marker's full scenario name, e.g. <c>S5-originate-never-answered</c>.</summary>
    public string Scenario { get; } = scenario;

    /// <summary>The scenario's short id, e.g. <c>S5</c>.</summary>
    public string ScenarioId => Scenario.Split('-', 2)[0];

    public CallSession Session { get; } = session;

    /// <summary>The domain events the manager published for this call, in order.</summary>
    public IReadOnlyList<SessionDomainEvent> DomainEvents => _domainEvents;

    /// <summary>The call's audit trail, as event types.</summary>
    public IReadOnlyList<CallSessionEventType> Trail => [.. Session.Events.Select(e => e.Type)];

    internal void Record(SessionDomainEvent evt) => _domainEvents.Add(evt);

    public override string ToString() =>
        $"{Scenario}: state={Session.State} cause={Session.HangupCause?.ToString() ?? "null"} "
        + $"dialingAt={SetOrNull(Session.DialingAt)} queuedAt={SetOrNull(Session.QueuedAt)} "
        + $"connectedAt={SetOrNull(Session.ConnectedAt)} talk={SetOrNull(Session.TalkTime)} "
        + $"trail=[{string.Join(">", Trail)}] "
        + $"events=[{string.Join(",", DomainEvents.Select(Describe))}]";

    private static string SetOrNull<T>(T? value) where T : struct => value.HasValue ? "set" : "null";

    private static string Describe(SessionDomainEvent evt) => evt switch
    {
        CallEndedEvent e => $"CallEnded(cause={e.Cause?.ToString() ?? "null"},talk={SetOrNull(e.TalkTime)})",
        _ => evt.GetType().Name,
    };
}

/// <summary>An exception the server's observer threw into the dispatcher, and the scenario it came from.</summary>
internal sealed record SwallowedObserverException(string Scenario, string EventType, Exception Exception)
{
    public override string ToString() =>
        $"[{Scenario}] {EventType}: {Exception.GetType().Name}: {Exception.Message}";
}

/// <summary>
/// One call shape of the queue-shape captures: its short id, its full name, the caller number that
/// places it, and whether the call joins a queue at all.
/// </summary>
internal sealed record QueueShape(string Id, string Name, string CallerNumber, bool JoinsAQueue);

/// <summary>
/// The seventeen call shapes of the queue-shape captures (<c>Recordings/asterisk-ami/README.md</c>),
/// in the order the captures run them: fifteen that join a queue and two controls that never do.
/// </summary>
internal static class QueueShapes
{
    /// <summary>
    /// The dialer's number. The dialer's customer leg is an originate from the PBX, and its
    /// <c>Newchannel</c> reports the caller number <c>&lt;unknown&gt;</c> on a <c>PJSIP/pstn-</c> channel,
    /// so that pair stands for this number (<see cref="Of"/>).
    /// </summary>
    public const string DialerCallerNumber = "7000";

    private const string UnknownCallerNumber = "<unknown>";
    private const string TrunkChannelPrefix = "PJSIP/pstn-";

    public static readonly IReadOnlyList<QueueShape> All =
    [
        new("L", "L-local-member", "5550001", JoinsAQueue: true),
        new("P", "P-pjsip-member", "5550002", JoinsAQueue: true),
        new("P2", "P2-pjsip-ringall", "5550003", JoinsAQueue: true),
        new("PI", "PI-ivr-then-pjsip-queue", "5550004", JoinsAQueue: true),
        new("PD", "PD-dialer-pjsip-queue", DialerCallerNumber, JoinsAQueue: true),
        new("A", "A-agentpool-local-n", "5550005", JoinsAQueue: true),
        new("AO", "AO-agentpool-local-optimized", "5550006", JoinsAQueue: true),
        new("F", "F-freepbx-local-dial", "5550007", JoinsAQueue: true),
        new("XC", "XC-confirm-rejected-abandon", "5550013", JoinsAQueue: true),
        new("AX", "AX-agentpool-ackcall-never-acked-abandon", "5550014", JoinsAQueue: true),
        new("X1", "X1-abandon-unanswered", "5550008", JoinsAQueue: true),
        new("X2", "X2-abandon-after-ivr-answer", "5550009", JoinsAQueue: true),
        new("X3", "X3-queue-timeout-exit", "5550010", JoinsAQueue: true),
        new("T", "T-receptionist-blind-transfer-to-queue", "5550015", JoinsAQueue: true),
        new("O", "O-queue-timeout-overflow-to-second-queue", "5550016", JoinsAQueue: true),
        new("D", "D-direct-dial-no-queue", "5550011", JoinsAQueue: false),
        new("I", "I-ivr-only-no-queue", "5550012", JoinsAQueue: false),
    ];

    private static readonly Dictionary<string, QueueShape> ByCallerNumber =
        All.ToDictionary(s => s.CallerNumber, StringComparer.Ordinal);

    /// <summary>The shape a channel with this caller number belongs to, or null for any other channel.</summary>
    public static QueueShape? Of(string? callerIdNum, string? channel)
    {
        var number = string.Equals(callerIdNum, UnknownCallerNumber, StringComparison.Ordinal)
            && channel?.StartsWith(TrunkChannelPrefix, StringComparison.Ordinal) == true
                ? DialerCallerNumber
                : callerIdNum;
        return number is null ? null : ByCallerNumber.GetValueOrDefault(number);
    }
}

/// <summary>
/// Queue counters, or a change in them: calls offered, answered, abandoned, and waiting.
/// </summary>
internal readonly record struct QueueCounters(int Offered, int Answered, int Abandoned, int Waiting)
{
    public static QueueCounters operator +(QueueCounters a, QueueCounters b) =>
        new(a.Offered + b.Offered, a.Answered + b.Answered, a.Abandoned + b.Abandoned, a.Waiting + b.Waiting);

    public static QueueCounters operator -(QueueCounters a, QueueCounters b) =>
        new(a.Offered - b.Offered, a.Answered - b.Answered, a.Abandoned - b.Abandoned, a.Waiting - b.Waiting);
}

/// <summary>
/// One shape of a queue-shape replay, scored against Asterisk's own verdict in the same capture:
/// what Asterisk reported on the shape's caller channel, and what the SDK published for the shape's
/// calls.
/// </summary>
internal sealed class QueueShapeScore(QueueShape shape)
{
    private readonly List<CallConnectedEvent> _connected = [];
    private readonly List<CallSession> _sessions = [];
    private QueueCounters _tracker;

    public QueueShape Shape { get; } = shape;

    /// <summary>app_queue's <c>AgentConnect</c> frames on the shape's caller channel.</summary>
    public int AsteriskConnects { get; internal set; }

    /// <summary>app_queue's <c>QueueCallerAbandon</c> frames on the shape's caller channel.</summary>
    public int AsteriskAbandons { get; internal set; }

    /// <summary>The <see cref="CallConnectedEvent"/>s the manager published for the shape's calls, in order.</summary>
    public IReadOnlyList<CallConnectedEvent> ConnectedEvents => _connected;

    /// <summary>How far the tracker's offered count moved on the shape's calls' events.</summary>
    public int Offered => _tracker.Offered;

    /// <summary>How far the tracker's answered count moved on the shape's calls' events.</summary>
    public int Answered => _tracker.Answered;

    /// <summary>How far the tracker's abandoned count moved on the shape's calls' events.</summary>
    public int Abandoned => _tracker.Abandoned;

    /// <summary>
    /// How many calls the shape left waiting in the tracker once its events were all handled; 0 when
    /// every visit it opened was closed.
    /// </summary>
    public int WaitingLeak => _tracker.Waiting;

    /// <summary>Exceptions the server's observer threw while dispatching the shape's frames.</summary>
    public int ObserverExceptions { get; internal set; }

    /// <summary>The SDK's answered and abandoned counts equal Asterisk's, and nothing is left waiting.</summary>
    public bool AgreesWithAsterisk =>
        Answered == AsteriskConnects && Abandoned == AsteriskAbandons && WaitingLeak == 0;

    /// <summary>
    /// The sessions the shape's calls opened, in the order they started, as the manager holds them once the
    /// replay has ended: a reader sees each call's final state, timestamps and audit trail.
    /// </summary>
    public IReadOnlyList<CallSession> Sessions => _sessions;

    internal void RecordConnected(CallConnectedEvent evt) => _connected.Add(evt);

    internal void RecordSession(CallSession session) => _sessions.Add(session);

    internal void RecordTrackerChange(QueueCounters change) => _tracker += change;

    public override string ToString()
    {
        var connected = string.Join(",", ConnectedEvents.Select(e => $"{e.QueueName ?? "-"}/{e.AgentId ?? "-"}"));
        var verdict = AgreesWithAsterisk ? "ok" : "WRONG";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Shape.Name,-42} asterisk(connect={AsteriskConnects},abandon={AsteriskAbandons}) CallConnectedEvent={ConnectedEvents.Count} offered={Offered} answered={Answered} abandoned={Abandoned} waitingLeak={WaitingLeak} swallowed={ObserverExceptions} connected=[{connected}] {verdict}");
    }
}

/// <summary>What a queue-shape replay produced: a score per shape, and every exception the dispatcher swallowed.</summary>
internal sealed class QueueShapeReplay(
    string fixture,
    IReadOnlyList<QueueShapeScore> shapes,
    QueueCounters unattributed,
    IReadOnlyList<SwallowedObserverException> observerExceptions)
{
    public string Fixture { get; } = fixture;

    /// <summary>One score per shape, in <see cref="QueueShapes.All"/> order.</summary>
    public IReadOnlyList<QueueShapeScore> Shapes { get; } = shapes;

    /// <summary>Tracker changes made on the events of a call that belongs to no shape.</summary>
    public QueueCounters Unattributed { get; } = unattributed;

    /// <summary>
    /// Every exception the server's observer threw, in delivery order. Its scenario is the shape id of
    /// the frame being dispatched, or <see cref="QueueShapeDispatch.Unattributed"/>.
    /// </summary>
    public IReadOnlyList<SwallowedObserverException> ObserverExceptions { get; } = observerExceptions;

    /// <summary>The score of shape <paramref name="id"/> (<c>"L"</c>, <c>"PD"</c>, <c>"O"</c>, …).</summary>
    public QueueShapeScore Shape(string id) =>
        Shapes.SingleOrDefault(s => string.Equals(s.Shape.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException($"No queue shape '{id}'.", nameof(id));

    /// <summary>The scorecard: one line per shape, then the disagreement count, for assertion messages.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Fixture} (swallowed observer exceptions: {ObserverExceptions.Count})").AppendLine();
        foreach (var score in Shapes)
            sb.Append("   ").Append(score).AppendLine();
        if (Unattributed != default)
            sb.Append(CultureInfo.InvariantCulture, $"   (unattributed tracker change: {Unattributed})").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"   => {Shapes.Count(s => !s.AgreesWithAsterisk)} shape(s) disagree with Asterisk");
        return sb.ToString();
    }
}

/// <summary>
/// What a queue-shape replay is dispatching right now, for a caller that attributes a measurement
/// taken synchronously inside the delivery — a <c>MeterListener</c> callback, for example — to a shape.
/// </summary>
/// <remarks>
/// Only the thread delivering the frame sees it: a measurement taken on any other thread (another
/// test's, or a continuation) reads <c>null</c>, so it is never charged to a shape.
/// </remarks>
internal sealed class QueueShapeDispatch
{
    /// <summary>The attribution of a frame, or a published event, that belongs to no shape.</summary>
    public const string Unattributed = "(unattributed)";

    private int _thread = -1;
    private string? _frameShape;
    private string? _connectedShape;
    private string? _frame;

    /// <summary>
    /// The shape a measurement taken now belongs to, or <c>null</c> outside a delivery on this thread.
    /// It is the shape of the call a <see cref="CallConnectedEvent"/> was last published for during
    /// this delivery, if any was; otherwise the shape of the frame being delivered (its
    /// <c>Uniqueid</c>'s caller number, or failing that its <c>Linkedid</c>'s). Either can be
    /// <see cref="Unattributed"/>.
    /// </summary>
    public string? CurrentShape => IsDispatchingHere ? _connectedShape ?? _frameShape : null;

    /// <summary>The AMI event type being delivered, or <c>null</c> outside a delivery on this thread.</summary>
    public string? CurrentFrame => IsDispatchingHere ? _frame : null;

    private bool IsDispatchingHere => _thread == Environment.CurrentManagedThreadId;

    internal void Begin(string? frameShape, string? frame)
    {
        _frameShape = frameShape ?? Unattributed;
        _connectedShape = null;
        _frame = frame ?? "";
        _thread = Environment.CurrentManagedThreadId;
    }

    internal void OnConnected(string? shape)
    {
        if (IsDispatchingHere)
            _connectedShape = shape ?? Unattributed;
    }

    internal void End()
    {
        _thread = -1;
        _frameShape = null;
        _connectedShape = null;
        _frame = null;
    }
}

/// <summary>
/// A snapshot the queue-reload captures' tap took: a <c>UserEvent</c> marker, then <c>Status</c>, then
/// <c>QueueStatus</c>, named by the <c>ActionID</c>s of its two answers.
/// </summary>
internal sealed record CapturedSnapshot(string StatusActionId, string QueueStatusActionId)
{
    /// <summary>
    /// The snapshot the tap named <paramref name="id"/> (<c>a1</c>, <c>b1</c>, <c>b2</c>, <c>c1</c>,
    /// <c>d1</c>): its answers carry the <c>ActionID</c>s <c>w2qr-&lt;id&gt;-st</c> and <c>w2qr-&lt;id&gt;-qs</c>.
    /// </summary>
    public static CapturedSnapshot Named(string id) => new($"w2qr-{id}-st", $"w2qr-{id}-qs");

    public override string ToString() => $"snapshot {StatusActionId}/{QueueStatusActionId}";
}

/// <summary>
/// Where a replay's outage starts: the first of the call's live frames it withholds. It ends at the
/// reload. <see cref="FromTheCallsFirstFrame"/> withholds the whole call up to the reload, so the SDK first
/// learns of it from the snapshot; <see cref="After"/> delivers the call through the
/// <paramref name="ordinal"/>th frame of type <paramref name="callerEvent"/> on the caller's own channel.
/// </summary>
internal sealed record Outage(string? AfterCallerEvent, int Ordinal)
{
    public static Outage FromTheCallsFirstFrame { get; } = new(null, 0);

    public static Outage After(string callerEvent, int ordinal = 1) => new(callerEvent, ordinal);
}

/// <summary>
/// One of app_queue's frames on a replayed call's caller channel: its type, its queue, the instant Asterisk
/// stamped on it, its <c>HoldTime</c> when it carries one, and whether the replay delivered it.
/// </summary>
internal sealed record CapturedQueueFrame(string EventType, string Queue, DateTimeOffset At, long? HoldTime, bool Delivered)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{EventType}({Queue}{(HoldTime is { } h ? $", HoldTime {h}" : "")}) at {At:HH:mm:ss.ffffff}{(Delivered ? "" : " withheld")}");
}

/// <summary>What a queue-reload replay produced for its one call.</summary>
internal sealed class QueueReloadReplay(
    string fixture,
    string callerNumber,
    IReadOnlyDictionary<string, QueueCounters> queues,
    IReadOnlyDictionary<string, TimeSpan> recordedWait,
    IReadOnlyList<CallQueuedEvent> queued,
    IReadOnlyList<CallConnectedEvent> connected,
    IReadOnlyList<double> samples,
    IReadOnlyList<CallSession> sessions,
    DateTimeOffset? reloadInstant,
    QueueEntryEvent? snapshotEntry,
    IReadOnlyList<CapturedQueueFrame> callerQueueFrames,
    IReadOnlyList<SwallowedObserverException> observerExceptions)
{
    public string Fixture { get; } = fixture;

    public string CallerNumber { get; } = callerNumber;

    /// <summary>The tracker's counters, per queue.</summary>
    public IReadOnlyDictionary<string, QueueCounters> Queues { get; } = queues;

    /// <summary>The <see cref="CallQueuedEvent"/>s the manager published, in order.</summary>
    public IReadOnlyList<CallQueuedEvent> Queued { get; } = queued;

    /// <summary>The <see cref="CallConnectedEvent"/>s the manager published, in order.</summary>
    public IReadOnlyList<CallConnectedEvent> Connected { get; } = connected;

    /// <summary>The <c>sessions.wait_time</c> samples recorded while the call's frames were delivered, in milliseconds.</summary>
    public IReadOnlyList<double> SamplesMs { get; } = samples;

    /// <summary>The sessions the manager opened, in order.</summary>
    public IReadOnlyList<CallSession> Sessions { get; } = sessions;

    /// <summary>The reload's instant, the snapshot marker's <c>Timestamp</c>; <c>null</c> when none ran.</summary>
    public DateTimeOffset? ReloadInstant { get; } = reloadInstant;

    /// <summary>The caller's <c>QueueEntry</c> in the reload's snapshot, if it listed one.</summary>
    public QueueEntryEvent? SnapshotEntry { get; } = snapshotEntry;

    /// <summary>app_queue's frames on the caller's channel, in capture order, withheld or not.</summary>
    public IReadOnlyList<CapturedQueueFrame> CallerQueueFrames { get; } = callerQueueFrames;

    /// <summary>Every exception the server's observer threw, in delivery order.</summary>
    public IReadOnlyList<SwallowedObserverException> ObserverExceptions { get; } = observerExceptions;

    /// <summary>The tracker's counters for <paramref name="queue"/>, or zeros for a queue it never saw.</summary>
    public QueueCounters Queue(string queue) => Queues.GetValueOrDefault(queue);

    /// <summary>The wait the tracker recorded for <paramref name="queue"/>: the sum over its answered visits.</summary>
    public TimeSpan RecordedWait(string queue) => recordedWait.GetValueOrDefault(queue);

    /// <summary>The caller's <paramref name="ordinal"/>th <c>QueueCallerJoin</c>, 1-based.</summary>
    public CapturedQueueFrame Join(int ordinal) => Nth("QueueCallerJoin", ordinal);

    /// <summary>app_queue's one <c>AgentConnect</c> for the caller.</summary>
    public CapturedQueueFrame AgentConnect => CallerQueueFrames.Single(f => f.EventType == "AgentConnect");

    /// <summary>How many <c>AgentConnect</c>s app_queue sent for the caller.</summary>
    public int AsteriskConnects => CallerQueueFrames.Count(f => f.EventType == "AgentConnect");

    /// <summary>How many <c>QueueCallerAbandon</c>s app_queue sent for the caller.</summary>
    public int AsteriskAbandons => CallerQueueFrames.Count(f => f.EventType == "QueueCallerAbandon");

    /// <summary>The snapshot's reported start of the caller's visit: the reload instant minus its <c>Wait</c>.</summary>
    public DateTimeOffset SnapshotReportedStart =>
        ReloadInstant is { } reload && SnapshotEntry?.Wait is { } wait
            ? reload - TimeSpan.FromSeconds(wait)
            : throw new InvalidOperationException($"{Fixture}: no reload ran, or its snapshot reported no Wait for the caller.");

    /// <summary>What the replay produced, for assertion messages.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Fixture}, caller {CallerNumber}").AppendLine();
        sb.Append("   asterisk: ").AppendJoin(" -> ", CallerQueueFrames).AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"   reload: {(ReloadInstant is { } r ? r.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture) : "none")}, "
            + $"snapshot entry: {(SnapshotEntry is { } e ? $"{e.Queue} Wait {e.Wait?.ToString(CultureInfo.InvariantCulture) ?? "absent"}" : "none")}").AppendLine();
        foreach (var (queue, c) in Queues.OrderBy(q => q.Key, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"   tracker {queue}: offered={c.Offered} answered={c.Answered} abandoned={c.Abandoned} waiting={c.Waiting} "
                + $"recordedWait={RecordedWait(queue).TotalMilliseconds:0.###}ms").AppendLine();
        }

        sb.Append("   CallQueuedEvent: ").AppendJoin(", ", Queued.Select(q =>
            string.Create(CultureInfo.InvariantCulture, $"{q.QueueName}@{q.Timestamp:HH:mm:ss.ffffff}"))).AppendLine();
        sb.Append("   CallConnectedEvent: ").AppendJoin(", ", Connected.Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{c.QueueName ?? "-"}@{c.Timestamp:HH:mm:ss.ffffff}"))).AppendLine();
        sb.Append("   samples: ").AppendJoin(", ", SamplesMs.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture) + "ms"));
        if (ObserverExceptions.Count > 0)
            sb.AppendLine().Append("   swallowed: ").AppendJoin("; ", ObserverExceptions);
        return sb.ToString();
    }

    private CapturedQueueFrame Nth(string eventType, int ordinal) =>
        CallerQueueFrames.Where(f => f.EventType == eventType).ElementAtOrDefault(ordinal - 1)
        ?? throw new InvalidOperationException($"{Fixture}: the caller has no {eventType} #{ordinal}.");
}
