using System.Collections.Concurrent;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.Pipeline;

/// <summary>
/// Hosted service that wires <see cref="AudioSocketServer.OnSessionStarted"/>
/// to <see cref="ISessionHandler.HandleSessionAsync"/>, spawning a handler
/// loop for each incoming AudioSocket session, and ending each session when its handler is done.
/// </summary>
/// <remarks>
/// <para>
/// The broker ends every session it handed on as soon as the handler's
/// <see cref="ISessionHandler.HandleSessionAsync"/> completes, whether it returned, threw or was
/// cancelled: it calls the session's <see cref="AudioSocketSession.HangupAsync"/>, which writes one
/// hangup frame if the session is still live and then closes it, and does nothing for a session the
/// handler or the caller already ended. On Asterisk 20 and later the call then continues in the
/// dialplan. A handler that wants to keep the line keeps running; one that returns early while still
/// using the session in the background loses its line. A host that subscribes its own
/// <see cref="AudioSocketServer.OnSessionStarted"/> handler next to the broker's on the same server
/// shares the session, so the broker's ending ends it for both.
/// </para>
/// <para>
/// Every handler runs under a token that belongs to the broker. The token passed to
/// <see cref="StartAsync"/> only means that the start was aborted, so it never reaches a handler.
/// Two things cancel the handler token: the token passed to <see cref="StopAsync"/> being cancelled,
/// whether it already is when the stop is called, becomes so while the stop waits, or becomes so
/// later, and <see cref="Dispose"/>.
/// </para>
/// <para>
/// A stop within its budget does not cancel the handler token. It ends every session it handed on
/// that is still live, with a hangup frame and then the close, so each handler sees its session end,
/// and then waits for the handlers to return. Once the token passed to the stop is cancelled — the
/// host's shutdown is no longer graceful — the stop cancels the handler token and returns without
/// waiting any further. So code that calls <see cref="StopAsync"/> directly with a token that is never
/// cancelled waits until every handler has returned, however long that takes. <see cref="Dispose"/>
/// does not wait: it cancels the handler token and returns.
/// </para>
/// <para>
/// Once <see cref="StopAsync"/> has been called, or the broker has been disposed, it hands no new
/// session to the handler. Such a session stays with the server that accepted it, which releases it
/// when it stops. However many times <see cref="StartAsync"/> is called, the broker hands each
/// session on once. The broker is not restartable: a start after a stop does not subscribe again,
/// just as a host never restarts a hosted service. A start after disposal throws
/// <see cref="ObjectDisposedException"/> and subscribes nothing; a stop after disposal completes and
/// does nothing.
/// </para>
/// </remarks>
public sealed class VoiceAiSessionBroker : IHostedService, IDisposable
{
    private readonly AudioSocketServer _server;
    private readonly ISessionHandler _handler;
    private readonly ILogger<VoiceAiSessionBroker> _logger;

    /// <summary>
    /// The source behind the token every handler runs under. The broker owns it because a handler
    /// keeps that token for the whole session: the token the host hands to <c>StartAsync</c> means the
    /// start was aborted, and the host releases the source behind it the moment the start returns, so
    /// a token borrowed from there can never be cancelled again.
    /// </summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// <c>_shutdown</c>'s token, read once, here. The getter throws once the source is released, and a
    /// session the server raises while the broker is being disposed can still reach the dispatcher; a
    /// token read before the release keeps working and reads as cancelled.
    /// </summary>
    private readonly CancellationToken _handlerToken;

    /// <summary>
    /// The one dispatcher the broker subscribes to the server, built once so the start, the stop and
    /// the disposal all add and remove the same delegate.
    /// </summary>
    private readonly Func<AudioSocketSession, ValueTask> _dispatch;

    /// <summary>
    /// Every handler run the broker started and that has not finished yet, ending included. An entry is
    /// added before its run starts and removed by the run itself once the handler has returned and the
    /// session has been ended, so a handler and an ending that both complete synchronously cannot remove
    /// it before it is added.
    /// </summary>
    private readonly ConcurrentDictionary<HandlerRun, byte> _running = new();

    /// <summary>Completed by the first <see cref="StopAsync"/> once it has ended the live sessions and starts waiting.</summary>
    private readonly TaskCompletionSource _waitingForHandlers = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The host's stop token wired onto <c>_shutdown</c>, kept so a second stop disposes the first
    /// registration instead of leaking it.
    /// </summary>
    private CancellationTokenRegistration _stopRegistration;

    /// <summary>
    /// Set by the first <see cref="StartAsync"/>, so no later start subscribes again, and by the first
    /// <see cref="StopAsync"/>, so a start after a stop does not subscribe either.
    /// </summary>
    private int _started;

    /// <summary>
    /// Set by the first <see cref="Dispose"/>, which every later call then ignores. The SDK's own
    /// registration (<c>TryAddSingleton</c> plus an <c>AddHostedService</c> factory that resolves the
    /// same singleton) makes the container dispose this instance twice, and a second pass would
    /// cancel the source the first one released.
    /// </summary>
    private int _disposed;

    /// <summary>Creates a new session broker.</summary>
    public VoiceAiSessionBroker(
        AudioSocketServer server,
        ISessionHandler handler,
        ILogger<VoiceAiSessionBroker> logger)
    {
        _server = server;
        _handler = handler;
        _logger = logger;
        _handlerToken = _shutdown.Token;
        _dispatch = Dispatch;
    }

    /// <summary>
    /// Completes when the broker's stop has ended the sessions that were still live and starts waiting
    /// for the handlers it started. It exists from construction, so a test can hold it before the stop
    /// is called and release a parked handler on this edge rather than on a time.
    /// </summary>
    internal Task WaitingForHandlers => _waitingForHandlers.Task;

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The broker has been disposed.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The parameter is deliberately unused: it means the start was aborted, and this start begins
        // nothing that can be aborted. Handlers get the token this broker owns.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _server.OnSessionStarted += _dispatch;

            // A disposal that ran between the check above and the subscription has already removed the
            // dispatcher it found, which was none: remove this one, so a disposed broker hands nothing on.
            if (Volatile.Read(ref _disposed) != 0)
                _server.OnSessionStarted -= _dispatch;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// While <paramref name="cancellationToken"/> is not cancelled, the stop ends every session it
    /// handed on that is still live, with a hangup frame and then the close, and waits for the handlers
    /// to return; it does not cancel the token they run under. Once <paramref name="cancellationToken"/>
    /// is cancelled — already, while the stop waits, or after it returned — the handler token is
    /// cancelled and the stop returns without waiting any further. A direct caller that passes a token
    /// that is never cancelled therefore waits until every handler has returned. A stop after disposal
    /// does nothing.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe first, so no session the server raises from here on reaches the handler, and the
        // Cancel() the registration below can run inline never meets a dispatcher still attached. A
        // start after this one finds the broker already started and does not subscribe again.
        _server.OnSessionStarted -= _dispatch;
        Interlocked.Exchange(ref _started, 1);

        if (Volatile.Read(ref _disposed) != 0)
            return; // the disposal already cancelled the handlers' token and released its source

        // The handlers' token is cancelled only when the host says the shutdown is no longer graceful.
        // The registration runs inline when the token arrives already cancelled, and cancels through a
        // helper that tolerates a source a concurrent disposal has released. Static callback with the
        // broker as state: no closure, no ExecutionContext.
        _stopRegistration.Dispose();
        _stopRegistration = cancellationToken.UnsafeRegister(
            static state => ((VoiceAiSessionBroker)state!).CancelHandlers(), this);

        var runs = _running.Keys.ToArray();
        if (!cancellationToken.IsCancellationRequested && runs.Length > 0)
        {
            // End every live session first, so each handler sees its session end instead of a
            // cancelled token. The session's write lock keeps each hangup frame whole against audio a
            // handler may still be writing. Bounded by the stop token like the wait below.
            var endings = new Task[runs.Length];
            for (var i = 0; i < runs.Length; i++)
                endings[i] = EndSessionAsync(runs[i], cancellationToken);

            await Task.WhenAll(endings).WaitAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _waitingForHandlers.TrySetResult();
        if (runs.Length == 0 || cancellationToken.IsCancellationRequested)
            return;

        var finished = new Task[runs.Length];
        for (var i = 0; i < runs.Length; i++)
            finished[i] = runs[i].Finished;

        // Returns when every handler has returned, or as soon as the stop token is cancelled: its
        // registration has then cancelled the handlers' token, and the stop does not wait any further.
        await Task.WhenAll(finished).WaitAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Ends every handler the broker started and releases the broker. It stops handing sessions on,
    /// as <see cref="StopAsync"/> does, then cancels the token every handler was given before
    /// releasing its source, so a handler that outlived the stop, such as one stuck in a provider
    /// call, is not left under a token nothing can cancel. It does not wait for any handler. Every
    /// call after the first is ignored.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _server.OnSessionStarted -= _dispatch;
        _stopRegistration.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Cancels the handlers' token for a stop that is no longer graceful. A stop token registered just
    /// before a concurrent disposal released the source can still fire afterwards; the disposal has
    /// already cancelled the token then, so there is nothing left to do.
    /// </summary>
    private void CancelHandlers()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Released by a concurrent disposal, which cancelled the handlers' token before releasing it.
        }
    }

    private ValueTask Dispatch(AudioSocketSession session)
    {
        // Registered before the run starts, so the stop can never miss a run the broker started. The
        // run starts inline: the handler is entered before this dispatcher returns to the server.
        var run = new HandlerRun(session);
        _running.TryAdd(run, 0);
        _ = RunAsync(run);
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(HandlerRun run)
    {
        try
        {
            await _handler.HandleSessionAsync(run.Session, _handlerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled handler is not a failure; it is not logged, as before the broker kept the run.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            VoiceAiLog.SessionError(_logger, run.Session.ChannelId, ex);
        }
        finally
        {
            // The handler is done, so nothing will speak on this line again: end it from the server's
            // side. A session the handler, the caller or the stop already ended is left as it is.
            await EndSessionAsync(run, CancellationToken.None).ConfigureAwait(false);
            _running.TryRemove(run, out _);
            run.Finish();
        }
    }

    /// <summary>
    /// Ends the run's session through its idempotent hangup and logs, once at Information, that the
    /// broker ended a session that was still live. Never throws: the run that calls it is a task nobody
    /// awaits, and the stop that calls it must go on to wait for the handlers.
    /// </summary>
    private async Task EndSessionAsync(HandlerRun run, CancellationToken ct)
    {
        var session = run.Session;
        try
        {
            var wasLive = session.IsConnected;
            await session.HangupAsync(ct).ConfigureAwait(false);

            // The stop and the run can both find the session live and race to end it; the hangup writes
            // one frame whoever wins, and the run's flag keeps the line in the log to one as well.
            if (wasLive && run.TryClaimEndingLog())
                VoiceAiLog.SessionEndedByBroker(_logger, session.ChannelId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The hangup tolerates a transport already gone; what is left (a stop token cancelled while
            // the hangup waited for a write, a teardown racing it) leaves the session to the next ending.
            VoiceAiLog.SessionEndFailed(_logger, session.ChannelId, ex);
        }
    }

    /// <summary>One handler run: the session it was handed, and a signal for when the run has finished.</summary>
    private sealed class HandlerRun(AudioSocketSession session)
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _endingLogged;

        public AudioSocketSession Session { get; } = session;

        /// <summary>Completes once the handler has returned and the session has been ended.</summary>
        public Task Finished => _finished.Task;

        public void Finish() => _finished.TrySetResult();

        /// <summary>True for the first caller only: the one that logs the broker's ending of this session.</summary>
        public bool TryClaimEndingLog() => Interlocked.Exchange(ref _endingLogged, 1) == 0;
    }
}
