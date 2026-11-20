using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Owns the token every session save runs under and hands it to the session manager. Registered once by every sessions
/// registration, single- and multi-server alike, so a host has exactly one owner of that token however many
/// registrations it makes. It attaches and detaches no server.
/// </summary>
/// <remarks>
/// <para>
/// The token is handed in <see cref="StartingAsync"/>, which the host runs for every hosted service before any
/// service's <c>StartAsync</c>, so it is in place before the single-server attach and before a host attaches the
/// servers of a pool in its own start. A caller that drives this service as a plain <see cref="IHostedService"/> hands
/// it in <see cref="StartAsync"/> instead.
/// </para>
/// <para>
/// The stop cancels nothing by itself: a graceful stop is exactly the case in which an in-flight save is still worth
/// its budget. The host's stop token is wired onto the source in <see cref="StoppedAsync"/>, which the host runs only
/// after every hosted service's <c>StopAsync</c> has returned, whether it stops them one after another or concurrently.
/// So the cancellation always follows every detach — the SDK's own and a host's in its own stop — and no server event
/// can start a save after it. A caller that drives this service as a plain <see cref="IHostedService"/> gets the
/// wiring in <see cref="StopAsync"/>. The disposal cancels the source before releasing it, so a save that outlived the
/// whole stop ends there instead of holding a token nothing could ever cancel.
/// </para>
/// </remarks>
internal sealed class SessionPersistenceTokenService(ICallSessionManager sessionManager)
    : IHostedLifecycleService, IDisposable
{
    /// <summary>
    /// The source behind the token every persistence call runs under. This service owns it because the manager keeps
    /// that token for the whole process: the token the host hands to <c>StartAsync</c> means the start was aborted, and
    /// the host releases the source behind it the moment the start returns, so a token borrowed from there can never be
    /// cancelled again.
    /// </summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Serializes the start entries, the stop wiring and <see cref="Dispose"/>. A stop reads <see cref="_disposed"/> and
    /// wires the stop token onto <see cref="_shutdown"/> under it, and the disposal sets the flag and releases both under
    /// it; a check of the flag without the lock lets a stop pass the check, the disposal release the source, and the stop
    /// then register on a released source that a later cancel of the host's token would hit.
    /// </summary>
    private readonly Lock _lifecycleGate = new();

    /// <summary>
    /// The host's stop token wired onto <see cref="_shutdown"/>, kept so a second stop disposes the first registration
    /// instead of leaking it.
    /// </summary>
    private CancellationTokenRegistration _stopRegistration;

    /// <summary>
    /// Set by <see cref="Dispose"/> under <see cref="_lifecycleGate"/>. <see cref="IDisposable"/> requires a second call
    /// to be ignored rather than throw, and <c>Cancel</c> on an already-released source throws
    /// <see cref="ObjectDisposedException"/>, so the release needs a gate, not just an ordering. A stop that finds it set
    /// does nothing.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Whether the host drives this service through its lifecycle: set when <see cref="StartingAsync"/> runs. Such a
    /// host runs <see cref="StoppedAsync"/> after every stop, so the wiring waits for it; a plain driver gets it in
    /// <see cref="StopAsync"/>. Read and written under <see cref="_lifecycleGate"/>.
    /// </summary>
    private bool _lifecycleDriven;

    /// <summary>Hands the token before any hosted service starts, and marks the host as a lifecycle host.</summary>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        Hand(lifecycle: true);
        return Task.CompletedTask;
    }

    /// <summary>Hands the token, as for a caller that starts this service on its own.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Hand(lifecycle: false);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Wires the host's stop token for a caller that drives this service as a plain <see cref="IHostedService"/>. On a
    /// lifecycle host it does nothing: the wiring waits for <see cref="StoppedAsync"/>, after every service's stop.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        WireStop(fromStopped: false, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wires the host's stop token on a lifecycle host, which runs this only after every hosted service's
    /// <c>StopAsync</c> has returned, so every detach precedes the cancellation.
    /// </summary>
    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        WireStop(fromStopped: true, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hands the manager the token this service owns. A start after disposal throws <see cref="ObjectDisposedException"/>
    /// and hands nothing. The start token is deliberately not taken: it means the start was aborted, and this start begins
    /// nothing that can be aborted.
    /// </summary>
    private void Hand(bool lifecycle)
    {
        if (sessionManager is not CallSessionManager csm)
            return;

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (lifecycle)
                _lifecycleDriven = true;
            csm.SetShutdownToken(_shutdown.Token);
        }
    }

    /// <summary>
    /// Wires the host's stop token onto the persistence source, from the entry point this host's kind calls for. A stop
    /// after disposal does nothing: it registers nothing on the token, so a later cancel of it reaches no released
    /// source.
    /// </summary>
    private void WireStop(bool fromStopped, CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _lifecycleDriven != fromStopped)
                return;

            // The source is not cancelled here. The registration cancels it when the host says the shutdown is no
            // longer graceful, and runs inline when that token arrives already cancelled — under the lock, so on a
            // source the disposal has not released. Static callback with the source as state: no closure, no
            // ExecutionContext.
            _stopRegistration.Dispose();
            _stopRegistration = cancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(), _shutdown);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            // Set first: the cancel below runs the persistence token's callbacks inline, and a stop one of them makes on
            // this thread re-enters the lock and must find the service disposed.
            _disposed = true;

            // Released before the source, and it waits for a callback another thread is running: once it returns,
            // nothing cancels the source but this method.
            _stopRegistration.Dispose();
            // Cancel before releasing: a save that outlived the whole stop ends here, instead of being left holding a
            // token nothing could ever cancel.
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }
}
