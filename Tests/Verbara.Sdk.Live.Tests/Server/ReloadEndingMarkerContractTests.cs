using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// Guards (green before and after the fix): the marker a call ended by a completed snapshot reconciliation carries, on
/// every route that reconciles one — the first load, the reconnect's reload and
/// <see cref="Live.Server.VerbaraServer.ReconcileChannelsAsync"/>. On each, the session's metadata reads
/// <c>cause</c> = <c>reload</c>, its <see cref="CallSession.HangupCause"/> is null, a <see cref="CallEndedEvent"/>
/// handler that looks the session up already sees the marker, and a call on the same server ended by an observed
/// <c>Hangup</c> never carries it.
/// </summary>
/// <remarks>
/// <para>
/// The key and the value are asserted as string literals, never through the session manager's private constants, so a
/// change of either fails here: other components (and a later public constant) build on the strings themselves.
/// </para>
/// <para>
/// A real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>, with a
/// <see cref="CallSessionManager"/> attached to its server. The call ended by the route is the one the snapshot no longer
/// lists; the observed call is opened and hung up by the peer's own <c>Newchannel</c> and <c>Hangup</c>.
/// </para>
/// </remarks>
public sealed class ReloadEndingMarkerContractTests
{
    private const string ServerId = "srv-1";
    private const string StateLoaded = "[LIVE] State loaded";
    private const string GoneUid = "1790633830.0";
    private const string GoneChannel = "PJSIP/pstn-00000000";
    private const string ObservedUid = "1790633840.0";
    private const string ObservedChannel = "PJSIP/pstn-00000001";

    /// <summary>What a test reads for one call.</summary>
    private sealed record Ending(string? CauseMetadata, HangupCause? HangupCause, string? CauseSeenAtCallEnded);

    [Fact]
    public async Task StartAsync_ShouldMarkTheCallItEndedCauseReloadWithNoHangupCause_WhenTheFirstLoadsSnapshotOmitsAHeldChannel()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        await using var rig = new MarkerRig(run);

        // Held before the first load, through the channel table's public entry point; the first Status lists nothing.
        run.Server.Channels.OnNewChannel(GoneUid, GoneChannel, ChannelState.Up, callerIdNum: "5552101", context: "from-pstn",
            linkedId: GoneUid);
        await run.StartServerAsync();
        await rig.ObservedCallAsync(peer);

        rig.Measured().Should().BeEquivalentTo(MarkerRig.Contract, MarkerRig.Because("the first load"));
    }

    [Fact]
    public async Task Reload_ShouldMarkTheCallItEndedCauseReloadWithNoHangupCause_WhenTheReconnectsSnapshotOmitsAHeldChannel()
    {
        var first = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(GoneUid, GoneChannel, LinkedId: GoneUid)],
        };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await using var rig = new MarkerRig(run);
        await run.StartServerAsync();

        first.CloseSession();
        await run.SecondSocketCreated.WaitAsync(Run.Bound);
        run.ReleaseSecondPeer();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        await rig.ObservedCallAsync(second);

        rig.Measured().Should().BeEquivalentTo(MarkerRig.Contract, MarkerRig.Because("the reconnect's reload"));
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldMarkTheCallItEndedCauseReloadWithNoHangupCause_WhenItsSnapshotOmitsAHeldChannel()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(GoneUid, GoneChannel, LinkedId: GoneUid)],
        };
        await using var run = await Run.ConnectAsync(peer);
        await using var rig = new MarkerRig(run);
        await run.StartServerAsync();

        peer.StatusChannels = [];
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        await rig.ObservedCallAsync(peer);

        rig.Measured().Should().BeEquivalentTo(MarkerRig.Contract, MarkerRig.Because("ReconcileChannelsAsync"));
    }

    /// <summary>
    /// A session manager attached to the run's server, and a <see cref="CallEndedEvent"/> handler that looks up each ended
    /// session and reads its <c>cause</c> metadata at that moment.
    /// </summary>
    private sealed class MarkerRig : IAsyncDisposable
    {
        private readonly Run _run;
        private readonly CallSessionManager _manager = new(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore());
        private readonly ConcurrentDictionary<string, string?> _causeAtCallEnded = new(StringComparer.Ordinal);
        private readonly IDisposable _subscription;

        public MarkerRig(Run run)
        {
            _run = run;
            _subscription = _manager.Events.Subscribe(new OnNext(e =>
            {
                if (e is CallEndedEvent ended)
                {
                    var session = _manager.GetById(ended.SessionId);
                    _causeAtCallEnded[session?.LinkedId ?? $"(no session {ended.SessionId})"] =
                        session?.Metadata.GetValueOrDefault("cause");
                }
            }));
            _manager.AttachToServer(run.Server, ServerId);
        }

        /// <summary>Opens a call with the peer's <c>Newchannel</c> and ends it with its <c>Hangup</c> (16, normal).</summary>
        public async Task ObservedCallAsync(BootingAsterisk peer)
        {
            var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _run.Server.Channels.ChannelRemoved += c =>
            {
                if (c.UniqueId == ObservedUid)
                    removed.TrySetResult();
            };
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(ObservedUid, ObservedChannel, ObservedUid));
            await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(ObservedUid, ObservedChannel, ObservedUid));
            await removed.Task.WaitAsync(Run.Bound);
        }

        /// <summary>The contract, for the call the route ended (<c>Gone</c>) and the call a Hangup ended (<c>Observed</c>).</summary>
        public static readonly object Contract = new
        {
            Gone = new Ending(CauseMetadata: "reload", HangupCause: null, CauseSeenAtCallEnded: "reload"),
            Observed = new Ending(CauseMetadata: null, HangupCause: Enums.HangupCause.NormalClearing, CauseSeenAtCallEnded: null),
        };

        public static string Because(string route) =>
            $"on {route}, the call a completed snapshot proved gone carries cause = reload and no hangup cause, already "
            + "visible to a CallEndedEvent handler; the call ended by an observed Hangup carries its cause and no marker";

        /// <summary>What the manager holds for both calls, shaped as <see cref="Contract"/>.</summary>
        public object Measured() => new { Gone = Read(GoneUid), Observed = Read(ObservedUid) };

        private Ending Read(string linkedId)
        {
            var session = _manager.GetByLinkedId(linkedId);
            return new Ending(
                session?.Metadata.GetValueOrDefault("cause"),
                session?.HangupCause,
                _causeAtCallEnded.TryGetValue(linkedId, out var seen) ? seen : "(no CallEndedEvent)");
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await _manager.DisposeAsync();
        }
    }

    /// <summary>A session subscriber that runs <c>onNext</c> on each notification.</summary>
    private sealed class OnNext(Action<SessionDomainEvent> onNext) : IObserver<SessionDomainEvent>
    {
        void IObserver<SessionDomainEvent>.OnNext(SessionDomainEvent value) => onNext(value);

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to do.
        }
    }

    /// <summary>A store that saves nothing; the tests read the manager.</summary>
    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}
