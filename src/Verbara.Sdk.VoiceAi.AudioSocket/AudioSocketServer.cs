using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.VoiceAi.AudioSocket.Diagnostics;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.AudioSocket;

/// <summary>
/// TCP server that accepts AudioSocket connections from Asterisk.
/// Implements <see cref="IHostedService"/> for DI lifecycle management.
/// </summary>
/// <remarks>
/// <para>
/// <b>One live session per channel id.</b> Asterisk does not keep the AudioSocket UUID unique: a
/// dialplan that runs <c>AudioSocket()</c> or <c>Dial(AudioSocket/…)</c> again with the id it saved,
/// a redirect or a transfer that re-enters the bot, all connect again with the same id, and they do
/// it within milliseconds of the previous connection closing, before this server has released it.
/// A connection that presents the id of a session that is still live therefore waits, up to one
/// second, for that session's hangup to run, and is then served. Its <see cref="OnSessionStarted"/> is
/// raised after every <see cref="AudioSocketSession.OnHangup"/> handler of the previous session has
/// returned, so a consumer that keys its own state by <see cref="AudioSocketSession.ChannelId"/> never
/// sees two live sessions under one id.
/// </para>
/// <para>
/// A connection whose id stays held past that wait (two calls configured with one UUID) is refused:
/// the server writes a hangup frame, so on Asterisk 20 and later the call goes on in the dialplan after
/// <c>AudioSocket()</c>, closes the connection and logs a warning that names the channel id. A
/// connection over <see cref="AudioSocketOptions.MaxConcurrentSessions"/> is refused the same way and
/// logged as the limit. A refused connection is never announced, counted or traced as a session.
/// </para>
/// <para>
/// <b>The limit is never passed.</b> Each channel id the server serves holds one place against
/// <see cref="AudioSocketOptions.MaxConcurrentSessions"/>, taken after the connection has identified
/// itself, in the same atomic step that checks the limit, so a burst of connections that identify at
/// once on many threads admits exactly the limit and refuses the rest. A connection that presents the id
/// of a session still ending shares that session's place: it is not refused for the limit while the
/// holder ends, and the two never count as two places. The place is given back exactly once, when the
/// last connection sharing it is released or refused, including a release that runs after a stop.
/// </para>
/// <para>
/// <b>Nothing is registered once the stop has begun.</b> A connection whose registration is attempted
/// after <see cref="StopAsync"/> began, including one that identified itself before, is refused with a
/// hangup frame, never announced or counted, and not logged as a refusal: the client missed nothing.
/// </para>
/// <para>
/// <b>Started once.</b> The server binds one listener and runs one accept loop in its lifetime. A start
/// while it is running changes nothing, so a host that starts the same instance twice (as the hosted
/// service <c>AddAudioSocketServer</c> registers and again by hand) keeps the one listener, and the stop
/// releases everything the server started. The server is not restartable: a start after a stop binds
/// nothing. A start after disposal throws <see cref="ObjectDisposedException"/>, and a stop after
/// disposal does nothing.
/// </para>
/// </remarks>
public sealed class AudioSocketServer : IHostedService, IAsyncDisposable
{
    /// <summary>The wait after the first of a run of failed accepts.</summary>
    internal static readonly TimeSpan InitialAcceptBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between failed accepts, however long the run.</summary>
    internal static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);

    private readonly AudioSocketOptions _options;
    private readonly ILogger<AudioSocketServer> _logger;
    private readonly TimeProvider _timeProvider;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, RegistryEntry> _sessions = new();

    /// <summary>The places <see cref="AudioSocketOptions.MaxConcurrentSessions"/> bounds.</summary>
    private readonly AudioSocketAdmission _admission = new();

    /// <summary>
    /// Guards <see cref="_placeShares"/>: whether a connection joins its id's place or takes a new one,
    /// and whether a release passes its place on or gives it back, are each decided under it.
    /// </summary>
    private readonly Lock _placesGate = new();

    /// <summary>
    /// For each channel id that holds a place, how many connections share it: the registered session
    /// and any connection presenting the same id that is waiting for it. The place is given back when
    /// the count reaches zero.
    /// </summary>
    private readonly Dictionary<Guid, int> _placeShares = [];

    /// <summary>
    /// How long a connection that presents the id of a live session waits for that session to be
    /// released before it is refused. Asterisk reconnects 0.1–7 ms after the previous connection ends
    /// (measured on 20.20.1, 22.9.0 and 23.4.1). 2.6.0's code released an ending session within 8.5 ms
    /// with the host idle and within 256 ms with the host saturated. Only a duplicate id pays the whole
    /// wait, as silence before its dialplan goes on.
    /// </summary>
    internal static readonly TimeSpan SameIdGrace = TimeSpan.FromSeconds(1);
    private readonly Meter _instanceMeter;

    /// <summary>
    /// Set by the first <see cref="DisposeAsync"/>, which every later call then ignores. The SDK's own
    /// registration (<c>TryAddSingleton</c> plus an <c>AddHostedService</c> factory that resolves the
    /// same singleton) makes the container dispose this instance twice, and a second pass would cancel
    /// the source the first one released.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Set once <see cref="DisposeAsync"/> has released <see cref="_cts"/> and the meter. The public
    /// <see cref="StopAsync"/> keys its no-op on this flag rather than on <see cref="_disposed"/>:
    /// disposal sets that flag before it runs its own stop, and runs that stop through
    /// <see cref="StopCoreAsync"/>, which no flag guards.
    /// </summary>
    private int _released;

    /// <summary>
    /// Guards the start and the stop's first step, so that a start and a stop or a disposal racing it
    /// see each other: a start either binds before the stop reads what to release, or finds the stop
    /// (or the disposal) already begun and binds nothing.
    /// </summary>
    private readonly Lock _lifecycleGate = new();

    /// <summary>
    /// Set, under <see cref="_lifecycleGate"/>, by the first start that bound its listener and by the
    /// first stop, whichever comes first. Once it is set, a start binds nothing.
    /// </summary>
    private bool _lifecycleBegun;

    /// <summary>Raised when a new AudioSocket session has been established and the UUID frame received.</summary>
    /// <remarks>Raised once per served connection, and never for a refused one. For a channel id that
    /// connects again, it is raised after every <see cref="AudioSocketSession.OnHangup"/> handler of the
    /// previous session has returned (see the class remarks).</remarks>
    public event Func<AudioSocketSession, ValueTask>? OnSessionStarted;

    /// <summary>
    /// The actual port the server is listening on. Available after <see cref="StartAsync"/>; 0 before it
    /// and once the server has stopped.
    /// </summary>
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <summary>Number of currently active sessions.</summary>
    /// <remarks>A session counts until every one of its <see cref="AudioSocketSession.OnHangup"/> handlers
    /// has returned, and holds its channel id until then.</remarks>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>Initializes a new instance.</summary>
    public AudioSocketServer(AudioSocketOptions options, ILogger<AudioSocketServer> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance whose accept backoff and per-connection UUID timeout both wait on
    /// <paramref name="timeProvider"/>, so a test can drive those waits with a fake clock.
    /// </summary>
    internal AudioSocketServer(AudioSocketOptions options, ILogger<AudioSocketServer> logger, TimeProvider timeProvider)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
        _instanceMeter = new Meter("Verbara.Sdk.VoiceAi.AudioSocket", "1.0.0");
    }

    /// <summary>
    /// Replaces the listener's accept when set. Settable by tests (via InternalsVisibleTo), so a test
    /// can make accepts fail on demand instead of exhausting file descriptors to get a failure.
    /// </summary>
    internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride { get; set; }

    /// <summary>
    /// Runs right after a connection's entry was added to the registry and before the server checks
    /// whether its stop has begun. Settable by tests (via InternalsVisibleTo), so a test can run the stop
    /// inside that window instead of hoping a race lands there.
    /// </summary>
    internal Action? AfterRegistryAdd { get; set; }

    /// <summary>The places currently taken against <see cref="AudioSocketOptions.MaxConcurrentSessions"/>.</summary>
    internal int PlacesHeld => _admission.Held;

    /// <summary>
    /// Binds the listener and starts the accept loop, the first time. A start while the server is running,
    /// or after it was stopped, completes and binds nothing.
    /// </summary>
    /// <param name="cancellationToken">Unused: this start begins nothing that can be aborted.</param>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The parameter is deliberately unused: it means the start was aborted, and this start begins
        // nothing that can be aborted. The accept loop gets the token this server owns, which the
        // server's own stop cancels.
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // A second start would replace the listener and the stopping source, and leave the first
            // listener and its loop serving out of the stop's reach. A start after a stop would bind a
            // listener of a server its owner has finished with.
            if (_lifecycleBegun)
                return Task.CompletedTask;

            var endpoint = new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.Port);
            var listener = new TcpListener(endpoint);
            try
            {
                listener.Start(_options.MaxConcurrentSessions);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing was bound, so nothing was started: a later start may try again.
                listener.Dispose();
                throw;
            }

            _cts = new CancellationTokenSource();
            _listener = listener;
            _lifecycleBegun = true;
            AudioSocketLog.ServerListening(_logger, _options.ListenAddress, _options.Port);

            // Created here, once per instance: only the one start that binds reaches this line.
            _instanceMeter.CreateObservableGauge<long>(
                "audiosocket.sessions.active",
                () => ActiveSessionCount,
                unit: "{sessions}",
                description: "Active AudioSocket sessions");

            // CancellationToken.None on the hand-off, and deliberately so. Task.Run's token skips a work
            // item that has not started yet, and the listener is already bound with this loop as the
            // only thing that serves it: a skipped hand-off leaves a listener that takes connections
            // into its backlog and never accepts one. The loop's token is read here rather than inside
            // the lambda. A DisposeAsync that gets there first releases _cts, whose Token getter would
            // then throw on the pool and fault a task nobody observes; a token read before the release
            // just reads as cancelled, and the loop ends at its first check.
            var token = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops accepting, releases the listener and ends every session the server serves. A stop after
    /// disposal completes and does nothing.
    /// </summary>
    /// <param name="cancellationToken">Unused: the stop waits for nothing that can outlast it.</param>
    public Task StopAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _released) != 0 ? Task.CompletedTask : StopCoreAsync();

    /// <summary>
    /// The stop itself, run by <see cref="StopAsync"/> and by <see cref="DisposeAsync"/>. Marks the
    /// server's lifetime begun before it reads what to release, so a start that has not bound yet binds
    /// nothing afterwards.
    /// </summary>
    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cts;
        TcpListener? listener;
        lock (_lifecycleGate)
        {
            _lifecycleBegun = true;
            cts = _cts;
            listener = _listener;
        }

        if (cts is not null)
            await CancelStoppingSourceAsync(cts).ConfigureAwait(false);

        listener?.Stop();

        // The cancel above comes first: a registration whose entry this loop does not see reads it after
        // its add and withdraws. An entry still pending is left to its registrant, which sees the cancel
        // and refuses the connection with a hangup frame; ending it here would close it frameless.
        // Each entry is claimed as the loop reaches it, after the previous one's disposal, as before.
        foreach (var entry in _sessions.Values.Where(e => e.ClaimForStop()))
            await entry.Session.DisposeAsync().ConfigureAwait(false);

        _sessions.Clear();
        AudioSocketLog.ServerStopped(_logger);
    }

    internal async Task AcceptLoopAsync(CancellationToken ct)
    {
        var backoff = InitialAcceptBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await AcceptAsync(ct).ConfigureAwait(false);
                backoff = InitialAcceptBackoff;

                // CancellationToken.None on the hand-off, and deliberately so. Task.Run's token cancels
                // a work item that has not started yet, and this work item carries the only reference to
                // a connection the server has already accepted: skip it and nothing ever closes that
                // connection — no owner, no counter, and a far end still attached to a server that has
                // forgotten it. Ownership passes to the handler unconditionally, which is why `ct`
                // travels with it as an argument instead of gating it here: the handler is what closes a
                // connection taken over while the server is stopping, at the no-UUID branch below,
                // rather than waiting the timeout out. Putting `ct` back on Task.Run re-opens the leak;
                // the sibling accept loop (AriOutboundListener.AcceptLoopAsync) passes None for the
                // same reason.
                _ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);
                continue;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AudioSocketLog.AcceptError(_logger, ex);
            }

            // The catch-all keeps the server accepting through a failure, but a failure that persists
            // (EMFILE, say) fails every accept at once, and without a pause this loop would spin and log
            // without bound. The wait doubles with each consecutive failure up to the cap, and a
            // successful accept above starts it over.
            try
            {
                await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
        }
    }

    /// <summary>
    /// Cancels the stopping source. A public stop that read the released flag just before a concurrent
    /// disposal finished can meet the source already released; that disposal's own stop cancelled it
    /// first, so there is nothing left to cancel.
    /// </summary>
    private static async Task CancelStoppingSourceAsync(CancellationTokenSource cts)
    {
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Released by a disposal whose own stop had already cancelled it.
        }
    }

    private ValueTask<TcpClient> AcceptAsync(CancellationToken ct) =>
        AcceptOverride?.Invoke(ct) ?? _listener!.AcceptTcpClientAsync(ct);

    /// <summary>
    /// Serves one accepted connection; <paramref name="ct"/> is the server's stopping token. Internal so
    /// a test can hand it a connection and its own token, then end the wait for the UUID frame at a
    /// moment it controls: by cancelling that token, or by moving a fake clock past the timeout.
    /// </summary>
    internal async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.ConnectionTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var stream = client.GetStream();
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: _options.ReceiveBufferSize));
            Guid channelId = default;
            var gotUuid = false;

            while (!linked.Token.IsCancellationRequested && !gotUuid)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    // The wait for the UUID frame is over: the timeout fired or the server is
                    // stopping. Neither is a connection error; the no-UUID check below sorts them.
                    break;
                }

                var buffer = result.Buffer;

                if (AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame) &&
                    frame.Type == AudioSocketFrameType.Uuid)
                {
                    channelId = AudioSocketFrameCodec.ParseUuid(frame.Payload.Span);
                    reader.AdvanceTo(buffer.Start);
                    gotUuid = true;
                }
                else
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }

                if (result.IsCompleted) break;
            }

            if (!gotUuid)
            {
                // A stopping server closes the connection quietly: the client missed no deadline.
                if (!ct.IsCancellationRequested)
                    AudioSocketLog.NoUuidFrame(_logger);

                client.Dispose();
                return;
            }

            var session = new AudioSocketSession(channelId, client, reader, _options.DefaultFormat, _logger);

            // Two refusals, each logged as what it is. Neither connection was ever a session, so neither
            // is counted, traced or timed: the accepted count moves only for a connection whose close
            // will move the closed count.
            // A connection that presents a held id is that call coming back. It shares its holder's
            // place, waits for the holder below and takes over from it, so it never raises the count,
            // and the limit must not turn it away while the holder is still ending.
            var place = TakePlace(channelId);
            if (place is null)
            {
                AudioSocketLog.SessionLimitReached(_logger, _options.MaxConcurrentSessions);
                await RefuseAsync(session).ConfigureAwait(false);
                return;
            }

            var registered = false;
            try
            {
                var waitStart = _timeProvider.GetTimestamp();
                switch (await RegisterAsync(channelId, session, waitStart, ct).ConfigureAwait(false))
                {
                    case Registration.Registered:
                        // Set before the read loop starts, so no hangup can fire without it. The session
                        // invokes it after every OnHangup handler, so a same-id connection waiting on
                        // HungUp is let in only once the consumer has finished with this one. The place
                        // goes back there whatever removed the entry, a stop's clearing included.
                        session.Released = () =>
                        {
                            ReleaseSession(session);
                            place.GiveBack();
                        };
                        registered = true;
                        break;
                    case Registration.StillHeld:
                        AudioSocketLog.ChannelIdInUse(
                            _logger, channelId, (long)_timeProvider.GetElapsedTime(waitStart).TotalMilliseconds);
                        await RefuseAsync(session).ConfigureAwait(false);
                        return;
                    default:
                        // The server is stopping: nothing to log, the client missed nothing. The hangup
                        // frame still lets the call go on in the dialplan.
                        await RefuseAsync(session).ConfigureAwait(false);
                        return;
                }
            }
            finally
            {
                // A connection that never became a session gives its share of the place back here; a
                // registered one gives it back when it is released.
                if (!registered)
                    place.GiveBack();
            }

            var sessionStart = Stopwatch.GetTimestamp();
            AudioSocketMetrics.ConnectionsAccepted.Add(1);
            using var activity = AudioSocketActivitySource.StartSession(channelId);

            session.OnHangup += () =>
            {
                AudioSocketMetrics.ConnectionsClosed.Add(1);
                AudioSocketMetrics.SessionDurationMs.Record(
                    Stopwatch.GetElapsedTime(sessionStart).TotalMilliseconds);
            };

            session.StartReadLoop();

            AudioSocketLog.SessionStarted(_logger, channelId);

            if (OnSessionStarted is not null)
                await OnSessionStarted(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AudioSocketLog.HandleConnectionError(_logger, ex);
            client.Dispose();
        }
    }

    private enum Registration { Registered, StillHeld, Stopping }

    /// <summary>
    /// Takes the place <paramref name="channelId"/> needs, or returns <see langword="null"/> when the
    /// limit is reached. A connection whose id already holds a place (its session is still ending, or
    /// another connection presenting it is on its way in) shares that place and takes no new one; any
    /// other takes a new place, in the one atomic step that checks the limit.
    /// </summary>
    private Place? TakePlace(Guid channelId)
    {
        lock (_placesGate)
        {
            if (_placeShares.TryGetValue(channelId, out var shares))
            {
                _placeShares[channelId] = shares + 1;
            }
            else
            {
                if (!_admission.TryEnter(_options.MaxConcurrentSessions))
                    return null;

                _placeShares[channelId] = 1;
            }
        }

        return new Place(this, channelId);
    }

    /// <summary>
    /// Drops one connection's share of <paramref name="channelId"/>'s place, and gives the place back
    /// when no connection shares it any more. A release that finds a same-id connection still sharing
    /// the place passes it on to that connection instead, in the same step.
    /// </summary>
    private void ReturnShare(Guid channelId)
    {
        lock (_placesGate)
        {
            if (!_placeShares.TryGetValue(channelId, out var shares))
                return; // unreachable: every share is returned once, through its own Place

            if (shares > 1)
            {
                _placeShares[channelId] = shares - 1;
                return;
            }

            _placeShares.Remove(channelId);
            _admission.Exit();
        }
    }

    /// <summary>Whether the server's stop has begun, as the connection's token or the server's own says.</summary>
    private bool IsStopping(CancellationToken ct) =>
        ct.IsCancellationRequested || (_cts?.IsCancellationRequested ?? false);

    /// <summary>
    /// Registers <paramref name="session"/> under <paramref name="channelId"/>. While another session
    /// holds the id, it waits for that session's release, up to <see cref="SameIdGrace"/> from
    /// <paramref name="waitStart"/> in all, and tries again: the holder of a re-entered id is already
    /// ending, and its release is the edge that lets this one in.
    /// </summary>
    /// <remarks>
    /// The stop is read before every attempt and again right after a successful one. The entry enters
    /// the registry pending and is admitted only once that second read finds the stop not begun: the
    /// stop cancels its token before it walks the registry, so an add the stop's walk does not see is
    /// one whose second read sees the cancel. A stop that reaches a pending entry leaves it to this
    /// method, which withdraws it and has the connection refused with a hangup frame; exactly one of the
    /// two ends the connection, and a refused one always gets its frame.
    /// </remarks>
    private async ValueTask<Registration> RegisterAsync(
        Guid channelId, AudioSocketSession session, long waitStart, CancellationToken ct)
    {
        var entry = new RegistryEntry(session);
        while (true)
        {
            if (IsStopping(ct))
                return Registration.Stopping;

            if (_sessions.TryAdd(channelId, entry))
            {
                AfterRegistryAdd?.Invoke();

                // The add must be visible before the stop is read, or this read and the stop's walk of
                // the registry could each miss the other's write.
                Interlocked.MemoryBarrier();
                if (!IsStopping(ct) && entry.TryAdmit())
                    return Registration.Registered;

                _sessions.TryRemove(new KeyValuePair<Guid, RegistryEntry>(channelId, entry));
                return Registration.Stopping;
            }

            if (!_sessions.TryGetValue(channelId, out var holder))
                continue; // released between the two calls: try again

            var left = SameIdGrace - _timeProvider.GetElapsedTime(waitStart);
            if (left <= TimeSpan.Zero)
                return Registration.StillHeld;

            try
            {
                await holder.Session.HungUp.WaitAsync(left, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The grace ran out with the holder still live: two calls share this id.
                return Registration.StillHeld;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The server is stopping; the caller closes this connection quietly.
                return Registration.Stopping;
            }
        }
    }

    /// <summary>
    /// Closes a connection the server will not serve, with a hangup frame first so that, on Asterisk 20
    /// and later, the call goes on in the dialplan. The session never started its read loop, so its
    /// hangup never fires and it releases nothing.
    /// </summary>
    private static ValueTask RefuseAsync(AudioSocketSession session) => session.EndFromServerAsync();

    /// <summary>
    /// Releases the registry entry of a session that has hung up: the release its
    /// <see cref="AudioSocketSession.Released"/> callback performs after every hangup handler, named in
    /// one place. The server's hangup handler records the close metrics itself, so calling
    /// this directly moves no process-wide instrument. Internal so a test can release a session it
    /// built through the internal constructor.
    /// </summary>
    /// <remarks>
    /// Each session releases its own entry and no other. The pair overload removes the entry only while
    /// it still maps to this session, so a hangup processed after the entry stopped being this
    /// session's (a stop cleared the table and a same-id connection registered since) leaves that
    /// other session registered, where a removal by key alone would unregister it. The key is the
    /// session's <see cref="AudioSocketSession.ChannelId"/>, which is get-only and set from the id the
    /// session registered under. A same-id connection waiting on this session is let in once the
    /// session's hangup has finished (<see cref="AudioSocketSession.HungUp"/>), not by anything here.
    /// </remarks>
    internal void ReleaseSession(AudioSocketSession session)
    {
        if (_sessions.TryGetValue(session.ChannelId, out var entry) && ReferenceEquals(entry.Session, session))
            _sessions.TryRemove(new KeyValuePair<Guid, RegistryEntry>(session.ChannelId, entry));
    }

    /// <summary>
    /// A registry entry: the session and where its registration stands. It enters pending, and moves
    /// once, either to admitted by its registrant or to claimed by a stop.
    /// </summary>
    private sealed class RegistryEntry(AudioSocketSession session)
    {
        private const int Pending = 0;
        private const int Admitted = 1;
        private const int ClaimedByStop = 2;

        private int _state = Pending;

        public AudioSocketSession Session { get; } = session;

        /// <summary>The registrant's move: true when the entry is now admitted, false when a stop claimed it first.</summary>
        public bool TryAdmit() =>
            Interlocked.CompareExchange(ref _state, Admitted, Pending) == Pending;

        /// <summary>
        /// The stop's move: claims a pending entry for its registrant to refuse, and returns true only for
        /// an admitted entry, which the stop itself ends.
        /// </summary>
        public bool ClaimForStop() =>
            Interlocked.CompareExchange(ref _state, ClaimedByStop, Pending) == Admitted;
    }

    /// <summary>One connection's share of its channel id's place, returned at most once.</summary>
    private sealed class Place(AudioSocketServer server, Guid channelId)
    {
        private int _returned;

        public void GiveBack()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                server.ReturnShare(channelId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The core, not the public stop: the public one is a no-op once the server is released, and
        // disposal must still run its own stop.
        await StopCoreAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _instanceMeter.Dispose();
        _listener = null;
        Volatile.Write(ref _released, 1);
    }
}
