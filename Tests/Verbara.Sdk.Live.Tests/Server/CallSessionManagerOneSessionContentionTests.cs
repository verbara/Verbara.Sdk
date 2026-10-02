using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// One call, two legs, two threads: a reload's reconciliation admits one leg from its snapshot while the
/// live event stream delivers the other, both carrying the same <c>linkedid</c>, and the call must have
/// one session holding both. Nothing a test can inject runs between the session manager's lookup of the
/// <c>linkedid</c> and its creation of a session, so the race is proven by contention: each round releases
/// the two admissions together through a <see cref="Barrier"/>, is joined under a bound so a deadlock fails
/// as a timeout, and has its counts checked. A round whose counts are wrong is a red round; the test asserts
/// there were none.
/// </summary>
/// <remarks>
/// It lives in <c>Verbara.Sdk.Live.Tests</c> because the snapshot route into the channel table is internal to
/// <c>Verbara.Sdk.Live</c>. The manager is built with its public constructor over a test-local
/// <see cref="SessionStoreBase"/>, since the default in-memory store is internal to Sessions.
/// </remarks>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CallSessionManagerOneSessionContentionTests : IAsyncLifetime
{
    private const int Rounds = 2_000;

    /// <summary>Failure bound for one round: two threads meeting at the barrier and admitting one leg each.</summary>
    private static readonly TimeSpan RoundBound = TimeSpan.FromSeconds(10);

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sut;
    private readonly ConcurrentQueue<SessionDomainEvent> _events = new();
    private readonly IDisposable _subscription;

    public CallSessionManagerOneSessionContentionTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sut = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore());
        _sut.AttachToServer(_server, "srv-1");
        _subscription = _sut.Events.Subscribe(new Collector(_events));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _subscription.Dispose();
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task OnChannelAdded_ShouldOpenOneSessionHoldingBothLegs_WhenTheSnapshotAndTheLiveStreamAdmitThemAtTheSameTime()
    {
        var channels = _server.Channels;
        var redRounds = 0;
        string? firstRed = null;

        for (var round = 0; round < Rounds; round++)
        {
            var linkedId = $"1700000000.{round}";
            var fromSnapshot = $"{linkedId};1";
            var live = $"{linkedId};2";
            var startedBefore = _events.OfType<CallStartedEvent>().Count();

            // The read began before either leg existed, so the live leg is newer than the snapshot and is
            // kept by the reconciliation, which admits only the leg the snapshot reports.
            using (var window = channels.OpenReadWindow())
            {
                await RaceAsync(
                    () => channels.ReconcileWithSnapshot(
                        [new ChannelSnapshotEntry(fromSnapshot, $"PJSIP/trunk-{round}", ChannelState.Up, LinkedId: linkedId)],
                        window),
                    () => channels.OnNewChannel(live, $"PJSIP/100-{round}", ChannelState.Up, linkedId: linkedId));
            }

            var started = _events.OfType<CallStartedEvent>().Count() - startedBefore;
            var snapshotLegsCall = _sut.GetByChannelId(fromSnapshot);
            var liveLegsCall = _sut.GetByChannelId(live);
            var oneSession = started == 1 && snapshotLegsCall is not null
                && ReferenceEquals(snapshotLegsCall, liveLegsCall)
                && ReferenceEquals(_sut.GetByLinkedId(linkedId), snapshotLegsCall);
            if (!oneSession)
            {
                redRounds++;
                firstRed ??= $"round {round}: {started} call(s) started, the legs in "
                    + $"{(ReferenceEquals(snapshotLegsCall, liveLegsCall) ? "one session" : "two sessions")}";
            }

            // Hang both legs up so the next round's reconciliation finds nothing of this one to remove.
            channels.OnHangup(fromSnapshot, HangupCause.NormalClearing);
            channels.OnHangup(live, HangupCause.NormalClearing);
        }

        redRounds.Should().Be(0,
            $"legs that share a linkedid are one call whichever thread reports each, so the call has one session "
            + $"holding both and is started once ({Rounds} rounds, both admissions released together)"
            + $"; first red round: {firstRed}");
    }

    /// <summary>
    /// Run <paramref name="first"/> and <paramref name="second"/> on two thread-pool threads released together
    /// by a barrier, and wait for both under <see cref="RoundBound"/>.
    /// </summary>
    private static async Task RaceAsync(Action first, Action second)
    {
        using var barrier = new Barrier(2);
        var one = Task.Run(() => RunAfter(barrier, first));
        var two = Task.Run(() => RunAfter(barrier, second));
        await Task.WhenAll(one, two).WaitAsync(RoundBound);
    }

    private static void RunAfter(Barrier barrier, Action step)
    {
        if (!barrier.SignalAndWait(RoundBound))
            throw new TimeoutException("the other thread of the round never reached the barrier");
        step();
    }

    private sealed class Collector(ConcurrentQueue<SessionDomainEvent> into) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => into.Enqueue(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    /// <summary>A store that keeps nothing; the test reads the manager's events and lookups.</summary>
    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}
