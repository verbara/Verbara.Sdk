using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// What the manager still holds of a server after <see cref="CallSessionManager.DetachFromServer(string, DetachedCalls)"/>:
/// a released server is let go of once the host drops it and no call of its id is still held; a kept server, a server
/// a held call still needs, and an instance another attached id names stay reachable.
/// </summary>
/// <remarks>
/// <para>The test never holds the server instance in its own frame. The instance is built, attached, driven and
/// disposed in <see cref="MethodImplOptions.NoInlining"/> helpers that hand back or take only a
/// <see cref="WeakReference"/>, so no local of the test (or of an async state machine) roots it, and its connection is
/// a hand-written fake, so no substitute library's recorded calls or thread-local state root it either.</para>
/// <para>Dropping the host's references means disposing the server, then dropping it: an undisposed
/// <see cref="VerbaraServer"/> stays reachable from its own instance meter, which the runtime's meter registry holds
/// until the meter is disposed. That root is the host's, never the session manager's.</para>
/// <para>A <c>Keep</c> control runs the same sequence and expects the instance alive, so a dead reference elsewhere is
/// evidence about the manager, never a harness that cannot see retention.</para>
/// </remarks>
public sealed class CallSessionManagerReleasedServerRetentionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // --- A released server is collectable ----------------------------------------------------------------------

    [Fact]
    public async Task DetachFromServer_ShouldLetTheServerBeCollected_WhenItsCallsAreReleasedAndTheHostDropsIt()
    {
        await using var manager = NewManager();
        var server = AttachNew(manager, "a");
        var call = Arrive(manager, server, "a", "r1");

        manager.DetachFromServer("a", DetachedCalls.Release).Should().ContainSingle()
            .Which.Should().BeSameAs(call, "premise: the release lets the server's one call go");
        DisposeHost(server);

        IsAlive(server).Should().BeFalse(
            "after a release that left no call of the id held, the manager must hold no reference to the server");
    }

    [Fact]
    public async Task DetachFromServer_ShouldKeepTheServerReachable_WhenItsCallsAreKept()
    {
        await using var manager = NewManager();
        var server = AttachNew(manager, "a");
        var call = Arrive(manager, server, "a", "k1");

        manager.DetachFromServer("a", DetachedCalls.Keep).Should().BeEmpty();
        manager.GetById(call.SessionId).Should().BeSameAs(call, "premise: a keep leaves the call held");
        DisposeHost(server);

        IsAlive(server).Should().BeTrue(
            "control: a kept attachment stays the id's latest, so the harness must see the server retained");
    }

    [Fact]
    public async Task DetachFromServer_ShouldLetTheKeptServerBeCollected_WhenTheIdIsReleasedAfterAKeep()
    {
        await using var manager = NewManager();
        var server = AttachNew(manager, "a");
        var call = Arrive(manager, server, "a", "kr");

        manager.DetachFromServer("a", DetachedCalls.Keep).Should().BeEmpty();
        manager.DetachFromServer("a", DetachedCalls.Release).Should().ContainSingle()
            .Which.Should().BeSameAs(call, "premise: the release lets the kept call go");
        DisposeHost(server);

        IsAlive(server).Should().BeFalse(
            "a release lets go of the attachment a previous keep left as the id's latest, not only of one it closed");
    }

    [Fact]
    public async Task DetachFromServer_ShouldKeepTheInstanceForTheOtherId_WhenOneOfTwoIdsSharingItIsReleased()
    {
        await using var manager = NewManager();
        var ended = new ConcurrentQueue<string>();
        using var subscription = manager.Events.Subscribe(new EndingRecorder(ended));
        var server = AttachNew(manager, "a", "a2");
        var callA = Arrive(manager, server, "a", "sh");
        var callA2 = manager.GetByLinkedId("a2", LinkedIdOf("sh"))
            ?? throw new InvalidOperationException("premise: the shared instance's arrival opens a call under a2 too");
        callA2.Should().NotBeSameAs(callA, "premise: one instance under two ids is two calls");
        var generationOfA2 = callA2.AttachmentGeneration;

        manager.DetachFromServer("a", DetachedCalls.Release).Should().ContainSingle()
            .Which.Should().BeSameAs(callA, "premise: releasing a lets go of a's call only");
        DisposeHost(server);

        var aliveThroughA2 = IsAlive(server);
        HangUp(server, CallerLeg("sh"));
        var a2Endings = ended.Count(id => id == callA2.SessionId);
        var a2Generation = callA2.AttachmentGeneration;
        manager.DetachFromServer("a2", DetachedCalls.Release).Should().BeEmpty("premise: a2's call has ended");
        var aliveAfterBoth = IsAlive(server);

        new { aliveThroughA2, a2Endings, a2Generation, aliveAfterBoth }.Should().BeEquivalentTo(
            new { aliveThroughA2 = true, a2Endings = 1, a2Generation = generationOfA2, aliveAfterBoth = false },
            "releasing a leaves the instance to a2's live attachment, which still decides a2's call; releasing a2 "
            + "too leaves the manager no reference to it");
    }

    // --- A call still held keeps the server --------------------------------------------------------------------

    [Fact]
    public async Task DetachFromServer_ShouldKeepTheServerUntilALaterRelease_WhenTheReleaseSkippedAHeldCall()
    {
        await using var manager = NewManager();
        var server = AttachNew(manager, "a");
        var call = Arrive(manager, server, "a", "hc");

        using var entered = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        Exception? holderFault = null;
        // The holder owns the call's lock and nothing of the server: a parked handler's own stack would root it.
        var holder = new Thread(() =>
        {
            try
            {
                call.SyncRoot.Enter();
                try
                {
                    entered.Set();
                    if (!exit.Wait(Bound))
                        throw new TimeoutException("the holder was never told to leave the call's lock");
                }
                finally
                {
                    call.SyncRoot.Exit();
                }
            }
            catch (Exception ex)
            {
                holderFault = ex;
            }
        }) { IsBackground = true, Name = "call-lock holder" };
        holder.Start();
        entered.Wait(Bound).Should().BeTrue("premise: the holder takes the call's lock");

        var first = manager.DetachFromServer("a", DetachedCalls.Release);
        exit.Set();
        holder.Join(Bound).Should().BeTrue("premise: the holder leaves the lock and ends");
        holderFault.Should().BeNull();
        first.Should().BeEmpty("premise: the walk skipped the call whose lock it did not get");
        manager.GetById(call.SessionId).Should().BeSameAs(call, "premise: the skipped call is still held");
        DisposeHost(server);

        var aliveWhileHeld = IsAlive(server);
        var second = manager.DetachFromServer("a", DetachedCalls.Release);
        var aliveAfterSecond = IsAlive(server);

        new { aliveWhileHeld, Second = second.Select(c => c.SessionId).ToArray(), aliveAfterSecond }.Should()
            .BeEquivalentTo(new { aliveWhileHeld = true, Second = new[] { call.SessionId }, aliveAfterSecond = false },
                "while a call of the id is still held the manager keeps the server it may need; the release that "
                + "lets the last call go lets go of the server");
    }

    // --- helpers -----------------------------------------------------------------------------------------------

    private static CallSessionManager NewManager() =>
        new(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());

    private static string CallerLeg(string id) => $"{id}-a";

    private static string LinkedIdOf(string id) => $"{id}-L";

    /// <summary>Builds a server over a fake connection, attaches it under every id given, and returns only a weak reference.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachNew(CallSessionManager manager, params string[] serverIds)
    {
        var server = new VerbaraServer(new QuietAmiConnection(), NullLogger<VerbaraServer>.Instance);
        foreach (var id in serverIds)
            manager.AttachToServer(server, id);
        return new WeakReference(server);
    }

    /// <summary>A one-leg inbound call on the instance: leg <c>{id}-a</c>, linkedid <c>{id}-L</c>; returns the call under <paramref name="serverId"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CallSession Arrive(CallSessionManager manager, WeakReference server, string serverId, string id)
    {
        Instance(server).Channels.OnNewChannel(CallerLeg(id), $"PJSIP/trunk-{id}-a", ChannelState.Up,
            context: "from-trunk", linkedId: LinkedIdOf(id));
        return manager.GetByLinkedId(serverId, LinkedIdOf(id))
            ?? throw new InvalidOperationException($"premise: no call holds {id} on {serverId}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HangUp(WeakReference server, string uniqueId) =>
        Instance(server).Channels.OnHangup(uniqueId, HangupCause.NormalClearing);

    /// <summary>The host disposes the server and drops its reference (see the remarks).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeHost(WeakReference server)
    {
        var disposal = Instance(server).DisposeAsync().AsTask();
        disposal.Wait(Bound).Should().BeTrue("premise: the server's disposal completes");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static VerbaraServer Instance(WeakReference server) =>
        server.Target as VerbaraServer ?? throw new InvalidOperationException("premise: the server is still alive");

    /// <summary>Two full blocking collections with the finalizers run between them; whether the instance survived.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference server)
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }

        return server.IsAlive;
    }

    private sealed class EndingRecorder(ConcurrentQueue<string> ended) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallEndedEvent)
                ended.Enqueue(value.SessionId);
        }

        public void OnError(Exception error)
        {
            // The manager's stream never faults.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed.
        }
    }

    /// <summary>
    /// A connection that is never connected and carries nothing: the server is only its live tables here, driven
    /// directly. Hand-written so nothing outside the server's own graph records a reference to it.
    /// </summary>
    private sealed class QuietAmiConnection : IAmiConnection
    {
        public AmiConnectionState State => AmiConnectionState.Disconnected;

        public string? AsteriskVersion => "22.0.0";

        public event Func<ManagerEvent, ValueTask>? OnEvent
        {
            add { }
            remove { }
        }

        public event Action? Reconnected
        {
            add { }
            remove { }
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action,
            CancellationToken cancellationToken = default) where TResponse : ManagerResponse =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IDisposable Subscribe(IObserver<ManagerEvent> observer) => NoSubscription.Instance;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoSubscription : IDisposable
        {
            public static readonly NoSubscription Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
