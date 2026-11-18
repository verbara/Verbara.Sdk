using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A reconnect reload keeps the server's event subscription unless that subscription's stream ended, so no event the
/// connection dispatches while the reload runs falls between an old observer and a new one.
/// </summary>
/// <remarks>
/// The connection is a substitute that keeps its observers the way <c>AmiConnection</c> does: a subscription lasts until
/// it is disposed, across sessions, and the dispatch reads the observers present at the moment it runs. Nothing waits on
/// the wall clock: every event is dispatched at a point of the reload the substitute itself observes.
/// </remarks>
public sealed class ReloadSubscriptionTests
{
    private const string HeldUid = "1790900000.1";
    private const string HeldName = "PJSIP/held-00000001";
    private const string LaterUid = "1790900000.2";
    private const string LaterName = "PJSIP/later-00000002";

    [Fact]
    public async Task ReloadNewSession_ShouldKeepItsSubscription_WhenTheStreamDidNotEnd()
    {
        var connection = new KeepingConnection();
        var server = connection.NewServer();
        await server.StartAsync();

        await connection.ReconnectAsync(server);
        await connection.ReconnectAsync(server);

        using (new AssertionScope())
        {
            connection.Subscriptions.Should().Be(1, "a stream that never ended is still delivering to the start's observer");
            connection.Disposals.Should().Be(0, "the subscription the start took is the server's until its disposal");
        }

        await server.DisposeAsync();
        connection.Disposals.Should().Be(1, "the disposal releases the one subscription");
    }

    /// <summary>
    /// The Hangup is dispatched at the first point of the reload where an event can fall through: when the old
    /// subscription is released, if the reload releases it, otherwise when the reload asks for its channels.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadNewSession_ShouldObserveAHangupDispatchedDuringIt_WhenTheStreamDidNotEnd(bool listedByTheSnapshot)
    {
        var connection = new KeepingConnection();
        var server = connection.NewServer();
        var removed = new List<AsteriskChannel>();
        server.Channels.ChannelRemoved += removed.Add;
        connection.StatusReply = [Leg(HeldUid, HeldName)];
        await server.StartAsync();
        server.Channels.GetByUniqueId(HeldUid).Should().NotBeNull("the start loaded the held channel");

        // The snapshot is read with the Hangup in flight: listed, it is older than the Hangup.
        connection.StatusReply = listedByTheSnapshot ? [Leg(HeldUid, HeldName)] : [];
        connection.DuringTheReload = () => connection.Dispatch(new HangupEvent
        {
            UniqueId = HeldUid,
            Channel = HeldName,
            Cause = (int)HangupCause.UserBusy,
        });
        await connection.ReconnectAsync(server);

        using (new AssertionScope())
        {
            connection.DispatchedDuringTheReload.Should().BeTrue("the Hangup was dispatched while the reload ran");
            server.Channels.GetByUniqueId(HeldUid).Should().BeNull(
                "the channel hung up while the reload ran; an older snapshot does not bring it back");
            removed.Should().ContainSingle("the channel ends once").Which.Should().Match<AsteriskChannel>(
                c => c.UniqueId == HeldUid && !c.RemovedByReload && c.HangupCause == HangupCause.UserBusy,
                "the Hangup reached the table, so the channel leaves with the cause Asterisk gave, not as a reload's removal");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadNewSession_ShouldSubscribeAgainBeforeItsLoad_WhenTheStreamEnded(bool failed)
    {
        var connection = new KeepingConnection();
        var server = connection.NewServer();
        var lost = 0;
        server.ConnectionLost += _ => Interlocked.Increment(ref lost);
        await server.StartAsync();
        var first = connection.Observers.Single();

        // A connection that ends its stream with the session: the observer is told, and released from the connection.
        if (failed)
            first.OnError(new IOException("session ended"));
        else
            first.OnCompleted();
        connection.Forget(first);

        var subscriptionsAtTheReloadsStatus = -1;
        connection.AtTheReloadsStatus = () => subscriptionsAtTheReloadsStatus = connection.Subscriptions;
        await connection.ReconnectAsync(server);
        connection.Dispatch(new NewChannelEvent { UniqueId = LaterUid, Channel = LaterName, ChannelState = "6" });

        using (new AssertionScope())
        {
            lost.Should().Be(1, "ending the stream still reports the loss");
            connection.Disposals.Should().Be(1, "the subscription whose stream ended is released once");
            subscriptionsAtTheReloadsStatus.Should().Be(2, "the server subscribes again before its reload reads anything");
            server.Channels.GetByUniqueId(LaterUid).Should().NotBeNull("the new subscription delivers the new session's events");
        }

        // A stream that has not ended since keeps the new subscription.
        connection.AtTheReloadsStatus = null;
        await connection.ReconnectAsync(server);
        connection.Subscriptions.Should().Be(2, "the second subscription's stream did not end");
        await server.DisposeAsync();
    }

    /// <summary>
    /// The premise the reload rests on, pinned: the SDK's own connection keeps an observer across a session loss and its
    /// reconnect, never completes or fails it, and hands it the new session's events.
    /// </summary>
    [Fact]
    public async Task AmiConnection_ShouldKeepDeliveringToAnObserverWithoutEndingIt_WhenItsSessionIsLostAndComesBack()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        var observer = new RecordingObserver(LaterUid);
        using var subscription = run.Connection.Subscribe(observer);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Connection.Reconnected += () => reconnected.TrySetResult();

        first.CloseSession();
        run.ReleaseSecondPeer();
        await reconnected.Task.WaitAsync(Run.Bound);
        (await second.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(LaterUid, LaterName, LaterUid)))
            .Should().BeTrue("the second session is live");
        await observer.Seen.Task.WaitAsync(Run.Bound);

        observer.Ended.Should().Be(0, "the connection neither completes nor fails an observer when its session ends");
    }

    private sealed class RecordingObserver(string awaitedUid) : IObserver<ManagerEvent>
    {
        private int _ended;

        public TaskCompletionSource Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Ended => Volatile.Read(ref _ended);

        public void OnNext(ManagerEvent value)
        {
            if (value is NewChannelEvent nce && nce.UniqueId == awaitedUid)
                Seen.TrySetResult();
        }

        public void OnError(Exception error) => Interlocked.Increment(ref _ended);
        public void OnCompleted() => Interlocked.Increment(ref _ended);
    }

    private static StatusEvent Leg(string uniqueId, string channel) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ChannelState"] = "6",
            ["ChannelStateDesc"] = "Up",
        },
    };

    /// <summary>
    /// A substituted <see cref="IAmiConnection"/> that keeps its observers until each subscription is disposed, and runs
    /// <see cref="DuringTheReload"/> once, at the first point of a reload where an event can fall through.
    /// </summary>
    private sealed class KeepingConnection
    {
        private readonly Lock _gate = new();
        private readonly List<IObserver<ManagerEvent>> _observers = [];
        private bool _reloading;
        private int _subscriptions;
        private int _disposals;

        public KeepingConnection()
        {
            Substitute.AsteriskVersion.Returns("21.0.0");
            Substitute.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(ci => Subscribe(ci.Arg<IObserver<ManagerEvent>>()));
            Substitute.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
                .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0)));
        }

        public IAmiConnection Substitute { get; } = NSubstitute.Substitute.For<IAmiConnection>();
        public IReadOnlyList<StatusEvent> StatusReply { get; set; } = [];
        public Action? DuringTheReload { get; set; }
        public Action? AtTheReloadsStatus { get; set; }
        public bool DispatchedDuringTheReload { get; private set; }
        public int Subscriptions => Volatile.Read(ref _subscriptions);
        public int Disposals => Volatile.Read(ref _disposals);

        public IReadOnlyList<IObserver<ManagerEvent>> Observers
        {
            get
            {
                lock (_gate)
                    return [.. _observers];
            }
        }

        public VerbaraServer NewServer() => new(Substitute, NullLogger<VerbaraServer>.Instance);

        /// <summary>Raises <see cref="IAmiConnection.Reconnected"/> and waits for the reload it starts to end.</summary>
        public async Task ReconnectAsync(VerbaraServer server)
        {
            lock (_gate)
                _reloading = true;
            Substitute.Reconnected += Raise.Event<Action>();
            await server.ReconnectReload.WaitAsync(Run.Bound);
            lock (_gate)
                _reloading = false;
        }

        /// <summary>Delivers to the observers present now, the way the connection's pump reads them.</summary>
        public void Dispatch(ManagerEvent evt)
        {
            foreach (var observer in Observers)
                observer.OnNext(evt);
        }

        public void Forget(IObserver<ManagerEvent> observer)
        {
            lock (_gate)
                _observers.Remove(observer);
        }

        private Unsubscriber Subscribe(IObserver<ManagerEvent> observer)
        {
            Interlocked.Increment(ref _subscriptions);
            lock (_gate)
                _observers.Add(observer);
            return new Unsubscriber(this, observer);
        }

        private void Released(IObserver<ManagerEvent> observer)
        {
            Interlocked.Increment(ref _disposals);
            Forget(observer);
            RunDuringTheReload();
        }

        private void RunDuringTheReload()
        {
            Action? during;
            lock (_gate)
            {
                if (!_reloading || DuringTheReload is null)
                    return;
                during = DuringTheReload;
                DuringTheReload = null;
            }

            DispatchedDuringTheReload = true;
            during();
        }

        private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
        {
            if (action is StatusAction)
            {
                RunDuringTheReload();
                bool reloading;
                lock (_gate)
                    reloading = _reloading;
                if (reloading)
                    AtTheReloadsStatus?.Invoke();
            }
            await Task.Yield();
            if (action is not StatusAction)
                yield break;
            foreach (var status in StatusReply)
                yield return status;
        }

        private sealed class Unsubscriber(KeepingConnection connection, IObserver<ManagerEvent> observer) : IDisposable
        {
            public void Dispose() => connection.Released(observer);
        }
    }
}
