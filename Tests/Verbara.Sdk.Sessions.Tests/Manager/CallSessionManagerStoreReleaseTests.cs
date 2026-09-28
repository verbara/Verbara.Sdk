using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Binds what the manager's release does to the store, beside what the functional tests
/// (<c>DefaultStoreReleaseTests</c>) bind for the default store on the ordinary routes.
///
/// <para>The default in-memory store keeps the manager's own session object, so it is told when the
/// manager releases a call, and lets go of it. A store that provides durability is told nothing it
/// acts on: it keeps its own retention, and the manager never deletes from it. And no save of a
/// released call reaches the store — including one that was already under way when the release
/// happened: with several servers attached, an arrival on one server runs the release walk while
/// another server's thread is saving the call it releases.</para>
///
/// <para>Retention is crossed on the manager's clock seam, never by waiting. The two concurrent cases
/// are ordered by construction, each from one side of the interleaving: a release that lands inside a
/// save (the store runs the other server's arrival, on a thread of its own, after the manager decided
/// to save and before the store's write), and a save that lands inside a release (the store runs a
/// late leg's departure, on a thread of its own, right after it has let go of the call and before the
/// release returns). Each waits for its thread to finish.</para>
/// </summary>
public sealed class CallSessionManagerStoreReleaseTests : IAsyncDisposable
{
    /// <summary>Bounds the wait on a test's second thread; never reached when it runs.</summary>
    private static readonly TimeSpan ThreadBound = TimeSpan.FromSeconds(10);

    private readonly SessionOptions _options = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly VerbaraServer _serverA = NewServer();
    private readonly VerbaraServer _serverB = NewServer();
    private CallSessionManager? _sut;

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null)
            await _sut.DisposeAsync();
        await _serverA.DisposeAsync();
        await _serverB.DisposeAsync();
    }

    [Fact]
    public async Task Release_ShouldLeaveADurableStoresRecordInPlace_WhenTheManagerReleasesTheCall()
    {
        var store = new DurableStoreStandIn();
        var sut = Attach(store);
        var call = EndedCall("v", _serverA);
        _clock.Advance(_options.CompletedRetention + TimeSpan.FromHours(1));
        EndedCall("n", _serverA);
        sut.GetById(call.SessionId).Should().BeNull("premise: the next call's ending released the call");

        var record = await store.GetAsync(call.SessionId, CancellationToken.None);

        new { Kept = record?.SessionId, store.Deletes }.Should().BeEquivalentTo(
            new { Kept = (string?)call.SessionId, Deletes = 0 },
            "a store that provides durability keeps its own retention: the manager's release neither "
            + "deletes the record the consumer registered that store to keep nor asks the store to");
    }

    [Fact]
    public async Task PersistAsync_ShouldLeaveNothingInTheDefaultStore_WhenAnArrivalOnAnotherServerReleasesTheCallDuringItsSave()
    {
        var store = new InterleavingStore();
        var sut = Attach(store);
        var call = OpenAnsweredCall("v", _serverA);
        _serverA.Channels.OnHangup("a-v", HangupCause.NormalClearing);
        _clock.Advance(_options.CompletedRetention + TimeSpan.FromHours(1));

        // The ending's own save of the call is the next one: while it is under way, a leg arrives on
        // the other server, and that arrival's walk finds the call past retention.
        var during = new DuringTheSave();
        store.ArmSaveOf(call.SessionId, () =>
        {
            var arrival = new Thread(() =>
            {
                try
                {
                    _serverB.Channels.OnNewChannel("n-1", "PJSIP/100-n-1", ChannelState.Ring, linkedId: "L-n");
                }
                catch (Exception ex)
                {
                    during.Failure = ex;
                }
            });
            arrival.Start();
            during.ArrivalFinished = arrival.Join(ThreadBound);
            during.ReleasedBeforeTheWrite = sut.GetById(call.SessionId) is null;
        });

        _serverA.Channels.OnHangup("c-v", HangupCause.NormalClearing);

        new { SaveHeldOpen = store.SaveStepRan, during.ArrivalFinished, during.Failure, during.ReleasedBeforeTheWrite }
            .Should().BeEquivalentTo(
                new { SaveHeldOpen = true, ArrivalFinished = true, Failure = (Exception?)null, ReleasedBeforeTheWrite = true },
                "premise: the ending's save was under way when the other server's arrival released the call");

        var byId = await store.Inner.GetAsync(call.SessionId, CancellationToken.None);
        var byLinkedId = await store.Inner.GetByLinkedIdAsync("L-v", CancellationToken.None);
        var arrived = await store.Inner.GetByLinkedIdAsync("L-n", CancellationToken.None);

        new { ById = byId?.SessionId, ByLinkedId = byLinkedId?.SessionId, ArrivedCallStored = arrived is not null }
            .Should().BeEquivalentTo(
                new { ById = (string?)null, ByLinkedId = (string?)null, ArrivedCallStored = true },
                "the store's write of the call landed after the manager released it and told the store "
                + "so; the manager checks again once the save is done and tells the store again, so the "
                + "write cannot outlast the release — while the arriving call is saved as usual");
    }

    [Fact]
    public async Task PersistAsync_ShouldLeaveNothingInTheDefaultStore_WhenALateLegLeavesOnAnotherThreadWhileTheCallIsReleased()
    {
        var store = new InterleavingStore();
        var sut = Attach(store);
        var call = EndedCall("v", _serverA);
        _serverA.Channels.OnNewChannel("x-v", "PJSIP/300-x-v", ChannelState.Ring, linkedId: "L-v");
        (sut.GetByChannelId("x-v")?.SessionId).Should().Be(call.SessionId,
            "premise: a leg carrying the ended call's linkedid joined it while it was retained");
        _clock.Advance(_options.CompletedRetention + TimeSpan.FromHours(1));

        // The release of the call is under way — the store has just let go of it — when the late leg
        // leaves on another thread, and its departure is saved.
        var during = new DuringTheRelease();
        store.ArmReleaseOf(call.SessionId, () =>
        {
            var departure = new Thread(() =>
            {
                try
                {
                    _serverA.Channels.OnHangup("x-v", HangupCause.NormalClearing);
                }
                catch (Exception ex)
                {
                    during.Failure = ex;
                }
            });
            departure.Start();
            during.DepartureFinished = departure.Join(ThreadBound);
            during.SavesOfTheCall = store.WritesOf(call.SessionId);
        });
        var writesBefore = store.WritesOf(call.SessionId);

        _serverB.Channels.OnNewChannel("n-1", "PJSIP/100-n-1", ChannelState.Ring, linkedId: "L-n");

        new
        {
            ReleaseStepRan = store.ReleaseStepRan,
            during.DepartureFinished,
            during.Failure,
            LateLegLeft = call.Participants.Single(p => p.UniqueId == "x-v").LeftAt.HasValue,
            Released = sut.GetById(call.SessionId) is null,
        }.Should().BeEquivalentTo(
            new { ReleaseStepRan = true, DepartureFinished = true, Failure = (Exception?)null, LateLegLeft = true, Released = true },
            "premise: the late leg left while the arrival on the other server was releasing the call");

        var byId = await store.Inner.GetAsync(call.SessionId, CancellationToken.None);

        new { ById = byId?.SessionId, WritesDuringTheRelease = during.SavesOfTheCall - writesBefore }
            .Should().BeEquivalentTo(
                new { ById = (string?)null, WritesDuringTheRelease = 0 },
                "the manager stops holding the call before it tells the store, so a save that checks "
                + "while the release is under way already finds the call released and writes nothing; "
                + "told the other way round, the save would find it held, write it back after the store "
                + "let go of it, and find it held again when it checked once more");
    }

    private CallSessionManager Attach(SessionStoreBase store)
    {
        _sut = new CallSessionManager(Options.Create(_options), NullLogger<CallSessionManager>.Instance, store, _clock);
        _sut.AttachToServer(_serverA, "srv-a");
        _sut.AttachToServer(_serverB, "srv-b");
        return _sut;
    }

    private static VerbaraServer NewServer()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("21.0.0");
        return new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    /// <summary>Two legs sharing <c>L-{tag}</c>, dialled and answered: one connected call.</summary>
    private CallSession OpenAnsweredCall(string tag, VerbaraServer server)
    {
        var linkedId = $"L-{tag}";
        server.Channels.OnNewChannel($"c-{tag}", $"PJSIP/trunk-c-{tag}", ChannelState.Ring,
            context: "from-trunk", linkedId: linkedId);
        server.Channels.OnNewChannel($"a-{tag}", $"PJSIP/100-a-{tag}", ChannelState.Ring, linkedId: linkedId);
        server.Channels.OnDialBegin($"c-{tag}", $"a-{tag}", $"PJSIP/100-a-{tag}", null);
        server.Channels.OnNewState($"a-{tag}", ChannelState.Up);
        return _sut!.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"premise: a session was opened for '{linkedId}'");
    }

    /// <summary>One connected call whose two legs both hang up.</summary>
    private CallSession EndedCall(string tag, VerbaraServer server)
    {
        var session = OpenAnsweredCall(tag, server);
        server.Channels.OnHangup($"a-{tag}", HangupCause.NormalClearing);
        server.Channels.OnHangup($"c-{tag}", HangupCause.NormalClearing);
        session.State.Should().Be(CallSessionState.Completed, $"premise: '{tag}' ended with both legs hung up");
        return session;
    }

    /// <summary>What happened inside the armed save, read after it; nothing is asserted inside it.</summary>
    private sealed class DuringTheSave
    {
        public bool ArrivalFinished { get; set; }

        public Exception? Failure { get; set; }

        public bool ReleasedBeforeTheWrite { get; set; }
    }

    /// <summary>What happened inside the armed release notice, read after it.</summary>
    private sealed class DuringTheRelease
    {
        public bool DepartureFinished { get; set; }

        public Exception? Failure { get; set; }

        public int SavesOfTheCall { get; set; }
    }

    /// <summary>
    /// A stand-in for a store that provides durability: it keeps what it is given until it is asked
    /// to delete it, counts those deletions, and — like every store outside this assembly — does not
    /// override the manager's release notice.
    /// </summary>
    private sealed class DurableStoreStandIn : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, CallSession> _records = new(StringComparer.Ordinal);
        private int _deletes;

        public int Deletes => Volatile.Read(ref _deletes);

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _records[session.SessionId] = session;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult(_records.GetValueOrDefault(sessionId));

        public override ValueTask DeleteAsync(string sessionId, CancellationToken ct)
        {
            Interlocked.Increment(ref _deletes);
            _records.TryRemove(sessionId, out _);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The default in-memory store with one step run at a chosen point: inside the armed session's next
    /// save, before the write, or inside the manager's next release notice for it, after
    /// <see cref="Inner"/> has let go of it. Every member forwards to <see cref="Inner"/>, the release
    /// notice included, so what <see cref="Inner"/> holds afterwards is what the default store would
    /// hold. Counts the writes it forwards, per session.
    /// </summary>
    private sealed class InterleavingStore : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, int> _writes = new(StringComparer.Ordinal);
        private (string SessionId, Action Step)? _inSave;
        private (string SessionId, Action Step)? _inRelease;

        public InMemorySessionStore Inner { get; } = new();

        /// <summary>Whether the step armed inside a save ran.</summary>
        public bool SaveStepRan { get; private set; }

        /// <summary>Whether the step armed inside a release notice ran.</summary>
        public bool ReleaseStepRan { get; private set; }

        public int WritesOf(string sessionId) => _writes.GetValueOrDefault(sessionId);

        public void ArmSaveOf(string sessionId, Action beforeTheWrite) => _inSave = (sessionId, beforeTheWrite);

        public void ArmReleaseOf(string sessionId, Action afterLettingGo) => _inRelease = (sessionId, afterLettingGo);

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            if (_inSave is { } armed && armed.SessionId == session.SessionId)
            {
                _inSave = null;
                SaveStepRan = true;
                armed.Step();
            }

            _writes.AddOrUpdate(session.SessionId, 1, (_, n) => n + 1);
            return Inner.SaveAsync(session, ct);
        }

        internal override void OnReleasedByManager(CallSession session)
        {
            Inner.OnReleasedByManager(session);

            if (_inRelease is { } armed && armed.SessionId == session.SessionId)
            {
                _inRelease = null;
                ReleaseStepRan = true;
                armed.Step();
            }
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            Inner.GetAsync(sessionId, ct);

        public override ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct) =>
            Inner.GetByLinkedIdAsync(linkedId, ct);

        public override ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct) =>
            Inner.GetActiveAsync(ct);

        public override ValueTask DeleteAsync(string sessionId, CancellationToken ct) =>
            Inner.DeleteAsync(sessionId, ct);
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
