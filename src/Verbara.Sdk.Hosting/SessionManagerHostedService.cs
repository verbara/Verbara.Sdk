using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Attaches the session engine to the single <see cref="VerbaraServer"/> and detaches it on stop.
/// </summary>
/// <remarks>
/// <para>
/// The attach happens in <see cref="StartingAsync"/>, which the host runs for every hosted service before any
/// service's <c>StartAsync</c>. <see cref="VerbaraServerHostedService"/>'s start runs the server's first load, which
/// announces every channel of Asterisk's <c>Status</c> snapshot — and every caller of its <c>QueueStatus</c> — on the
/// server's events; a manager that attaches after that load never hears it, so the calls already in progress when
/// the process starts would have no session until the next reconnect. Attached first, the manager opens each of them
/// through the path a reconnect reload takes.
/// </para>
/// <para>
/// A caller that drives this service as a plain <see cref="IHostedService"/> — <see cref="StartAsync"/> alone — still
/// attaches, in <see cref="StartAsync"/>. Whichever of the two runs first attaches and the other does nothing: a second
/// attach under the same id would replace the subscriptions without removing the first ones, and every event would
/// then be handled twice. A stop detaches, so a start after a stop attaches again.
/// </para>
/// </remarks>
internal sealed class SessionManagerHostedService(
    ICallSessionManager sessionManager,
    VerbaraServer server) : IHostedLifecycleService, IDisposable
{
    /// <summary>
    /// The source behind the token every persistence call runs under. This service owns it because
    /// the manager keeps that token for the whole process: the token the host hands to
    /// <c>StartAsync</c> means the start was aborted, and the host releases the source behind it the
    /// moment the start returns, so a token borrowed from there can never be cancelled again.
    /// </summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// The host's stop token wired onto <c>_shutdown</c>, kept so a second stop disposes the first
    /// registration instead of leaking it.
    /// </summary>
    private CancellationTokenRegistration _stopRegistration;

    /// <summary>
    /// Serializes the attach, <see cref="StopAsync"/> and <see cref="Dispose"/>. A stop reads
    /// <see cref="_disposed"/> and wires the stop token onto <see cref="_shutdown"/> under it, and the
    /// disposal sets the flag and releases both under it; a check of the flag without the lock lets a stop
    /// pass the check, the disposal release the source, and the stop then register on a released source
    /// that a later cancel of the host's token would hit.
    /// </summary>
    private readonly Lock _lifecycleGate = new();

    /// <summary>
    /// Set by <see cref="Dispose"/> under <see cref="_lifecycleGate"/>. <see cref="IDisposable"/> requires
    /// a second call to be ignored rather than throw, and <c>Cancel</c> on an already-released source throws
    /// <see cref="ObjectDisposedException"/> — so the release needs a gate, not just an ordering. A stop
    /// that finds it set does nothing.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Whether the manager is attached to the server now. Set by the first of <see cref="StartingAsync"/> and
    /// <see cref="StartAsync"/>, cleared by the stop's detach, read and written under <see cref="_lifecycleGate"/>.
    /// </summary>
    private bool _attached;

    /// <summary>
    /// Attaches the manager before any hosted service starts, so the server's first load reaches it.
    /// </summary>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        Attach();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attaches the manager when <see cref="StartingAsync"/> did not, as for a caller that starts this service on its
    /// own.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Attach();
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Attaches the manager once. A start after disposal throws <see cref="ObjectDisposedException"/> and attaches
    /// nothing, whichever entry point it comes through and whether or not an earlier one attached.
    /// </summary>
    private void Attach()
    {
        // The start token is deliberately not taken: it means the start was aborted, and this start begins nothing
        // that can be aborted. The manager gets the token this service owns.
        if (sessionManager is not CallSessionManager csm)
            return;

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached)
                return;

            csm.SetShutdownToken(_shutdown.Token);
            csm.AttachToServer(server, "default");
            _attached = true;
        }
    }

    /// <summary>
    /// Detaches the manager and wires the host's stop token onto the persistence token. A stop after
    /// disposal does nothing: it registers nothing on the token, so a later cancel of it reaches no
    /// released source.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return Task.CompletedTask;

            // Detach first, so no server event can start a new save while the shutdown budget runs.
            if (sessionManager is CallSessionManager csm)
            {
                csm.DetachFromServer("default");
                _attached = false;
            }

            // The source is not cancelled here — a graceful stop is exactly the case in which an
            // in-flight save is still worth its budget. The registration cancels it when the host says
            // the shutdown is no longer graceful, and runs inline when that token arrives already
            // cancelled — under the lock, so on a source the disposal has not released. Static callback
            // with the source as state: no closure, no ExecutionContext.
            _stopRegistration.Dispose();
            _stopRegistration = cancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(), _shutdown);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            // Set first: the cancel below runs the persistence token's callbacks inline, and a stop one
            // of them makes on this thread re-enters the lock and must find the service disposed.
            _disposed = true;

            // Released before the source, and it waits for a callback another thread is running: once it
            // returns, nothing cancels the source but this method.
            _stopRegistration.Dispose();
            // Cancel before releasing: a save that outlived the whole stop ends here, instead of being
            // left holding a token nothing could ever cancel.
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }
}
