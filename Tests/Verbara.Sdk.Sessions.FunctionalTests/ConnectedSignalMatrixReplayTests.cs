using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the connected-signal-matrix captures (<c>Recordings/asterisk-ami/connected-signal-matrix-*</c>, the README's
/// <i>The connected-signal-matrix captures</i>) one call at a time, and checks when a call is announced connected and
/// where it waits: one <see cref="CallConnectedEvent"/> each time the call enters <see cref="CallSessionState.Connected"/>
/// from dialing, ringing, queued or transferring, none for a return from hold or for a call a reload opened, and a
/// connected or transferring call that joins a queue waits in <see cref="CallSessionState.Queued"/>.
/// </summary>
/// <remarks>
/// Every count is over the whole call, as the capture's own <c>CaptureCheckpoint</c> frames divide it. The live arm
/// delivers every live frame of the call to a fresh server and manager; the reload arm withholds the frames before the
/// call's last checkpoint that took a <c>Status</c> and <c>QueueStatus</c> snapshot, answers a reconnect reload with
/// those snapshots, and delivers the rest.
/// </remarks>
public sealed class ConnectedSignalMatrixReplayTests
{
    private const string Repeat = "connected-signal-matrix-repeat-asterisk-22.12.0.raw";

    /// <summary>Every capture, with every call it keeps and the number of announcements the call publishes in total.</summary>
    public static TheoryData<string, string, int> AnnouncementsPerCall
    {
        get
        {
            var data = new TheoryData<string, string, int>();
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixCaptures)
            {
                data.Add(fixture, "plain-01", 1);
                data.Add(fixture, "requeue-01", 2);
            }

            data.Add(Repeat, "hold-01", 1);
            data.Add(Repeat, "txbridge-01", 2);
            data.Add(Repeat, "aq-01", 2);
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures)
            {
                data.Add(fixture, "queue-txdial-01", 2);
                data.Add(fixture, "requeue-hangup-01", 1);
                data.Add(fixture, "aq-hangup-01", 1);
            }

            return data;
        }
    }

    /// <summary>Every capture and call, for the checks that hold on every call alike.</summary>
    public static TheoryData<string, string> EveryCall
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var row in AnnouncementsPerCall)
                data.Add((string)row[0], (string)row[1]);
            return data;
        }
    }

    /// <summary>The calls that wait in a queue after a connection, with the checkpoint taken while they wait.</summary>
    public static TheoryData<string, string, string> WaitingAfterAConnection
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixCaptures)
                data.Add(fixture, "requeue-01", "wait2");
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures)
            {
                data.Add(fixture, "requeue-hangup-01", "wait2");
                data.Add(fixture, "aq-hangup-01", "wait");
            }

            return data;
        }
    }

    /// <summary>The calls a blind transfer returns to connected, by bridge entry or by dial outcome.</summary>
    public static TheoryData<string, string> TransferReturns
    {
        get
        {
            var data = new TheoryData<string, string> { { Repeat, "txbridge-01" } };
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures)
                data.Add(fixture, "queue-txdial-01");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AnnouncementsPerCall))]
    public async Task CallConnectedEvent_ShouldBePublishedOncePerConnection_WhenACapturedCallIsReplayed(
        string fixture, string step, int expected)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Swallowed.Should().Be(0, "no observer throws on {0} {1}", fixture, step);
        run.Session.Should().NotBeNull("premise: the replay opens the caller's call");
        run.Announcements.Should().HaveCount(expected,
            "each entry into connected from dialing, ringing, queued or transferring is announced once, and nothing else is");
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task CallConnectedEvent_ShouldFindTheCallConnected_WhenEachAnnouncementOfACapturedCallIsDelivered(
        string fixture, string step)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Announcements.Should().NotBeEmpty("premise: every call of the theory is announced at least once");
        run.Announcements.Should().OnlyContain(a => a.State == CallSessionState.Connected && a.ConnectedAt != null,
            "a subscriber reading the call on its announcement finds it connected, with its connected time set");
        if (run.Announcements.Count == 0)
            return;
        run.Announcements[0].ConnectedAt.Should().Be(run.Announcements[0].Event.Timestamp,
            "the call's connected time is its first announcement's timestamp");
        run.Session!.ConnectedAt.Should().Be(run.Announcements[0].Event.Timestamp,
            "a later connection keeps the first connected time");
    }

    [Theory]
    [MemberData(nameof(WaitingAfterAConnection))]
    public async Task CallSession_ShouldWaitQueued_WhenAConnectedOrTransferringCapturedCallJoinsAQueue(
        string fixture, string step, string checkpoint)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt(checkpoint).Should().Be(CallSessionState.Queued,
            "a call that joins a queue after a connection waits there in queued, as a call joining from its first states does");
        run.StatesWhenQueued.Should().NotBeEmpty("premise: the call joins a queue after it was connected")
            .And.OnlyContain(s => s == CallSessionState.Queued, "every join announces a call that is waiting in queued");
    }

    [Fact]
    public async Task CallSession_ShouldPassThroughQueued_WhenACapturedCallAnsweredByADialIsThenQueuedAndTaken()
    {
        var run = await SignalMatrix.LiveAsync(Repeat, "aq-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StatesWhenQueued.Should().Equal([CallSessionState.Queued],
            "the answered call joins q-second and waits there in queued");
        run.Announcements.Select(a => a.Event.QueueName).Should().Equal([null, "q-second"],
            "the dial's answer is announced first, naming no queue, and the member's connection second, naming the queue");
    }

    [Fact]
    public async Task CallConnectedEvent_ShouldNotBePublished_WhenACapturedConnectedCallIsHeldAndResumed()
    {
        var run = await SignalMatrix.LiveAsync(Repeat, "hold-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Held.Should().Be(1, "premise: the call is put on hold once");
        run.Resumed.Should().Be(1, "premise: the call is taken off hold once");
        (run.Announcements.Count - run.AnnouncementsAt("held")).Should().Be(0,
            "a return from hold is not an answer: no announcement after the held checkpoint");
    }

    [Theory]
    [MemberData(nameof(TransferReturns))]
    public async Task CallConnectedEvent_ShouldBePublishedOnceMore_WhenACapturedCallReturnsToConnectedAfterATransfer(
        string fixture, string step)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Transferred.Should().Be(1, "premise: the call is blind-transferred once");
        (run.Announcements.Count - run.AnnouncementsAt("answered")).Should().Be(1,
            "the transfer target's answer returns the call to connected, and that return is announced once");
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task CallConnectedEvent_ShouldNotBePublished_WhenAReloadOpensTheCapturedCallConnected(
        string fixture, string step)
    {
        var run = await SignalMatrix.ReloadAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Session.Should().NotBeNull("premise: the reload opens the caller's call");
        run.Announcements.Should().BeEmpty("a call a reload opens is not announced, and nothing connects it afterwards");
        run.Session!.ConnectedAt.Should().BeNull("no connection of the call's own was observed");
    }
}

/// <summary>One call of a connected-signal-matrix capture, replayed live or across a reload.</summary>
internal static class SignalMatrix
{
    internal sealed record Call(string Step, IReadOnlyList<ManagerEvent> Frames, string CallerUniqueId,
        IReadOnlyList<(int Index, string Name, string? StatusId, string? QueueStatusId)> Checkpoints);

    internal sealed record Announcement(CallConnectedEvent Event, CallSessionState State, DateTimeOffset? ConnectedAt);

    internal sealed class Run
    {
        private readonly string? _callerUniqueId;
        private readonly Func<string, CallSession?> _byChannel;
        private readonly Dictionary<string, (CallSessionState? State, int Announcements)> _checkpoints =
            new(StringComparer.Ordinal);

        public Run(string? callerUniqueId, Func<string, CallSession?> byChannel)
        {
            _callerUniqueId = callerUniqueId;
            _byChannel = byChannel;
        }

        public CallSession? Session { get; private set; }

        public List<Announcement> Announcements { get; } = [];

        public List<CallSessionState> StatesWhenQueued { get; } = [];

        public int Held { get; private set; }

        public int Resumed { get; private set; }

        public int Transferred { get; private set; }

        public List<CallEndedEvent> Endings { get; } = [];

        /// <summary>Every notification the call published, in the order it was delivered.</summary>
        public List<SessionDomainEvent> Log { get; } = [];

        public int Swallowed { get; set; }

        public CallSessionState? StateAt(string checkpoint) =>
            _checkpoints.TryGetValue(checkpoint, out var at)
                ? at.State
                : throw new InvalidOperationException($"premise: the call has a {checkpoint} checkpoint");

        public int AnnouncementsAt(string checkpoint) =>
            _checkpoints.TryGetValue(checkpoint, out var at)
                ? at.Announcements
                : throw new InvalidOperationException($"premise: the call has a {checkpoint} checkpoint");

        public void Bind()
        {
            if (Session is null && _callerUniqueId is not null)
                Session = _byChannel(_callerUniqueId);
        }

        public void On(SessionDomainEvent evt)
        {
            Bind();
            if (Session is null || !string.Equals(evt.SessionId, Session.SessionId, StringComparison.Ordinal))
                return;
            Log.Add(evt);
            switch (evt)
            {
                case CallConnectedEvent c: Announcements.Add(new Announcement(c, Session.State, Session.ConnectedAt)); break;
                case CallQueuedEvent: StatesWhenQueued.Add(Session.State); break;
                case CallHeldEvent: Held++; break;
                case CallResumedEvent: Resumed++; break;
                case CallTransferredEvent: Transferred++; break;
                case CallEndedEvent e: Endings.Add(e); break;
            }
        }

        public void Mark(string name)
        {
            Bind();
            _checkpoints[name] = (Session?.State, Announcements.Count);
        }

        public string Describe() =>
            $"session={Session?.SessionId ?? "(none)"} state={Session?.State} connectedAt={Session?.ConnectedAt?.ToString("O", CultureInfo.InvariantCulture)} "
            + $"checkpoints=[{string.Join(", ", _checkpoints.Select(c => $"{c.Key}:{c.Value.State}/{c.Value.Announcements}"))}] "
            + $"announcements=[{string.Join(", ", Announcements.Select(a => $"{a.Event.QueueName}|{a.Event.AgentInterface}|{a.State}"))}] "
            + $"queued=[{string.Join(", ", StatesWhenQueued)}] held={Held} resumed={Resumed} transferred={Transferred} "
            + $"cause={Session?.HangupCause?.ToString() ?? "null"} talk={Session?.TalkTime} "
            + $"endings=[{string.Join(", ", Endings.Select(e => e.Cause?.ToString() ?? "null"))}]";
    }

    private static string? Field(ManagerEvent e, string key) => e.RawFields?.GetValueOrDefault(key);

    private static bool IsUserEvent(ManagerEvent e, string name) =>
        string.Equals(Field(e, "UserEvent"), name, StringComparison.Ordinal);

    /// <summary>A frame a client other than the tap receives: no answer to the tap's actions, no capture user event.</summary>
    private static bool IsLive(ManagerEvent f) =>
        Field(f, "ActionID") is null && !(Field(f, "UserEvent")?.StartsWith("Capture", StringComparison.Ordinal) ?? false);

    /// <summary>The frames of <paramref name="step"/>, from its <c>CaptureMarker</c> to the next one.</summary>
    public static async Task<Call> ReadAsync(string fixture, string step) =>
        Slice(await AmiCaptureReplay.ReadCaptureAsync(fixture), fixture, step);

    /// <summary>The frames of <paramref name="step"/> in a capture held in memory (a copy a test altered).</summary>
    public static async Task<Call> ReadAsync(byte[] capture, string step) =>
        Slice(await AmiCaptureReplay.ReadCaptureAsync(capture), "(in memory)", step);

    private static Call Slice(IReadOnlyList<ManagerEvent> capture, string fixture, string step)
    {
        var frames = new List<ManagerEvent>();
        string? current = null;
        foreach (var f in capture)
        {
            if (IsUserEvent(f, "CaptureMarker"))
            {
                current = Field(f, "Step");
                continue;
            }

            if (string.Equals(current, step, StringComparison.Ordinal))
                frames.Add(f);
        }

        var caller = frames.Where(f => IsUserEvent(f, "CaptureOutcome")).Select(f => Field(f, "CallerUniqueid"))
            .SingleOrDefault() ?? throw new InvalidOperationException($"premise: {fixture} keeps step {step} with its outcome");
        var checkpoints = frames.Select((f, i) => (f, i)).Where(x => IsUserEvent(x.f, "CaptureCheckpoint"))
            .Select(x => (x.i, Field(x.f, "Name") ?? "", Field(x.f, "StatusId"), Field(x.f, "QueueStatusId")))
            .ToList();
        return new Call(step, frames, caller, checkpoints);
    }

    /// <summary>Delivers every live frame of the call, in capture order, to a fresh server and manager.</summary>
    public static async Task<Run> LiveAsync(string fixture, string step) => await LiveAsync(await ReadAsync(fixture, step));

    /// <summary>Delivers every live frame of <paramref name="call"/>, in order, to a fresh server and manager.</summary>
    public static async Task<Run> LiveAsync(Call call)
    {
        await using var rig = await AmiCaptureReplay.ReplayRig.StartAsync(new SessionOptions());
        var run = new Run(call.CallerUniqueId, rig.Manager.GetByChannelId);
        using var subscription = rig.Manager.Events.Subscribe(run.On);
        foreach (var f in call.Frames)
        {
            if (IsUserEvent(f, "CaptureCheckpoint"))
            {
                run.Mark(Field(f, "Name") ?? "");
                continue;
            }

            if (!IsLive(f))
                continue;

            if (rig.Deliver(f) is not null)
                run.Swallowed++;
            run.Bind();
        }

        return run;
    }

    /// <summary>
    /// Withholds the call's frames before its last snapshot checkpoint, answers a reconnect reload with the
    /// <c>Status</c> and <c>QueueStatus</c> the capture took there, and delivers the rest.
    /// </summary>
    public static async Task<Run> ReloadAsync(string fixture, string step) =>
        await ReloadAsync(await ReadAsync(fixture, step));

    /// <summary>The reload arm of <see cref="ReloadAsync(string, string)"/> over <paramref name="call"/>.</summary>
    public static async Task<Run> ReloadAsync(Call call)
    {
        var at = call.Checkpoints.Last(c => c.StatusId is not null && c.QueueStatusId is not null);
        var status = call.Frames.Where(f => Field(f, "ActionID") == at.StatusId).ToList();
        var queueStatus = call.Frames.Where(f => Field(f, "ActionID") == at.QueueStatusId).ToList();
        var reloadAt = call.Frames.Select((f, i) => (f, i)).First(x => Field(x.f, "ActionID") == at.StatusId).i;

        var connection = new ReloadableConnection();
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        var run = new Run(call.CallerUniqueId, manager.GetByChannelId);
        try
        {
            using var subscription = manager.Events.Subscribe(run.On);
            manager.AttachToServer(server, "reload-replay");
            await server.StartAsync();
            for (var i = reloadAt; i < call.Frames.Count; i++)
            {
                var f = call.Frames[i];
                if (i == reloadAt)
                {
                    await connection.ReloadAsync(status, queueStatus);
                    run.Mark("reloaded");
                    continue;
                }

                if (IsUserEvent(f, "CaptureCheckpoint"))
                {
                    run.Mark(Field(f, "Name") ?? "");
                    continue;
                }

                if (!IsLive(f))
                    continue;

                try
                {
                    connection.Observer.OnNext(f);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    run.Swallowed++;
                }

                run.Bind();
            }
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }

        return run;
    }

    /// <summary>
    /// Delivers the call's live frames up to <paramref name="checkpoint"/>, then answers a reconnect reload with the
    /// list ends of the <c>Status</c> and <c>QueueStatus</c> the capture took there and none of their rows: an Asterisk
    /// that no longer holds any of the call's channels. Nothing after the checkpoint is delivered.
    /// </summary>
    public static async Task<Run> GoneAtAsync(Call call, string checkpoint)
    {
        var at = call.Checkpoints.Single(c => string.Equals(c.Name, checkpoint, StringComparison.Ordinal)
            && c.StatusId is not null && c.QueueStatusId is not null);
        var statusEnd = call.Frames.Where(f => Field(f, "ActionID") == at.StatusId
            && string.Equals(f.EventType, "StatusComplete", StringComparison.Ordinal)).ToList();
        var queueStatusEnd = call.Frames.Where(f => Field(f, "ActionID") == at.QueueStatusId
            && string.Equals(f.EventType, "QueueStatusComplete", StringComparison.Ordinal)).ToList();
        if (statusEnd.Count != 1 || queueStatusEnd.Count != 1)
            throw new InvalidOperationException($"premise: the {checkpoint} snapshots end with their list-complete frames");

        var connection = new ReloadableConnection();
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        var run = new Run(call.CallerUniqueId, manager.GetByChannelId);
        try
        {
            using var subscription = manager.Events.Subscribe(run.On);
            manager.AttachToServer(server, "reload-replay");
            await server.StartAsync();
            for (var i = 0; i < at.Index; i++)
            {
                var f = call.Frames[i];
                if (IsUserEvent(f, "CaptureCheckpoint"))
                {
                    run.Mark(Field(f, "Name") ?? "");
                    continue;
                }

                if (!IsLive(f))
                    continue;

                try
                {
                    connection.Observer.OnNext(f);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    run.Swallowed++;
                }

                run.Bind();
            }

            run.Mark(checkpoint);
            await connection.ReloadAsync(statusEnd, queueStatusEnd);
            run.Mark("gone");
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }

        return run;
    }
}
