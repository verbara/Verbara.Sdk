using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.Pipeline;

/// <summary>
/// Hosted service that wires <see cref="AudioSocketServer.OnSessionStarted"/>
/// to <see cref="ISessionHandler.HandleSessionAsync"/>, spawning a handler
/// loop for each incoming AudioSocket session.
/// </summary>
/// <remarks>
/// <para>
/// Every handler runs under a token that belongs to the broker. The token passed to
/// <see cref="StartAsync"/> only means that the start was aborted, so it never reaches a handler.
/// Two things cancel the handler token: the token passed to <see cref="StopAsync"/> being cancelled,
/// whether it already is when the stop is called or becomes so later because the host's shutdown
/// ran out of grace, and <see cref="Dispose"/>. A stop within its budget leaves running handlers
/// running: the <see cref="AudioSocketServer"/>'s own stop, which a host runs after the broker's,
/// ends the sessions a graceful shutdown ends.
/// </para>
/// <para>
/// Once <see cref="StopAsync"/> has returned, or the broker has been disposed, it hands no new
/// session to the handler. Such a session stays with the server that accepted it, which releases it
/// when it stops. However many times <see cref="StartAsync"/> is called, the broker hands each
/// session on once. The broker is not restartable: a start after a stop does not subscribe again,
/// just as a host never restarts a hosted service.
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
    /// The host's stop token wired onto <c>_shutdown</c>, kept so a second stop disposes the first
    /// registration instead of leaking it.
    /// </summary>
    private CancellationTokenRegistration _stopRegistration;

    /// <summary>Set by the first <see cref="StartAsync"/>, so no later start subscribes again.</summary>
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

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The parameter is deliberately unused: it means the start was aborted, and this start begins
        // nothing that can be aborted. Handlers get the token this broker owns.
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _server.OnSessionStarted += _dispatch;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe first, so no session the server raises from here on reaches the handler, and the
        // Cancel() the registration below can run inline never meets a dispatcher still attached.
        _server.OnSessionStarted -= _dispatch;

        // The source is not cancelled here: a graceful stop leaves running handlers running, and the
        // server's own stop, which a host runs next, ends their sessions. The registration cancels it
        // when the host says the shutdown is no longer graceful, and runs inline when that token
        // arrives already cancelled. Static callback with the source as state: no closure, no
        // ExecutionContext.
        _stopRegistration.Dispose();
        _stopRegistration = cancellationToken.UnsafeRegister(
            static state => ((CancellationTokenSource)state!).Cancel(), _shutdown);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ends every handler the broker started and releases the broker. It stops handing sessions on,
    /// as <see cref="StopAsync"/> does, then cancels the token every handler was given before
    /// releasing its source, so a handler that outlived the stop, such as one stuck in a provider
    /// call, is not left under a token nothing can cancel. Every call after the first is ignored.
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

    private ValueTask Dispatch(AudioSocketSession session)
    {
        _ = _handler.HandleSessionAsync(session, _handlerToken)
            .AsTask()
            .ContinueWith(
                t => VoiceAiLog.SessionError(_logger, session.ChannelId, t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        return ValueTask.CompletedTask;
    }
}
