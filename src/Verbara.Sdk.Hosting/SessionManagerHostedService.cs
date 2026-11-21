using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Attaches the session manager the host resolves to the single <see cref="VerbaraServer"/> and detaches it on stop,
/// through <see cref="ICallSessionManager"/>: the SDK's engine, a decorator over it, or a manager of the host's own.
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
/// attaches, in <see cref="StartAsync"/>. Whichever of the two runs first attaches and the other does nothing, so the
/// manager the host resolves sees one attach per start whatever its type: the engine takes a repeated attach of the same
/// server under the same id as a no-op, but a decorator or a manager of the host's own would see the second call. A stop
/// detaches, so a start after a stop attaches again.
/// </para>
/// <para>
/// The stop detaches only an attachment this service's start installed. When the SDK's engine the host reaches already
/// held <c>"default"</c> for this server before the attach, and the attach reached that engine (directly or through a
/// decorator), the host installed the id first: the attach was a no-op on the engine, and the stop leaves the host's
/// attachment in place. A start whose attach threw or never ran installed nothing, and its stop detaches nothing. An
/// attach that went to a manager other than that engine, or a host attach made after start, is this service's to
/// release.
/// </para>
/// </remarks>
internal sealed class SessionManagerHostedService(
    ICallSessionManager sessionManager,
    VerbaraServer server,
    CallSessionManager? engine) : IHostedLifecycleService, IDisposable
{
    /// <summary>
    /// Builds the service as the unit tests do, with the engine read from the manager itself: the manager when it is the
    /// SDK's engine, else none. The sessions registrations pass the engine <see cref="SessionEngine.Resolve"/> finds.
    /// </summary>
    public SessionManagerHostedService(ICallSessionManager sessionManager, VerbaraServer server)
        : this(sessionManager, server, sessionManager as CallSessionManager)
    {
    }

    /// <summary>
    /// Serializes the attach, <see cref="StopAsync"/> and <see cref="Dispose"/>, so a start that races the disposal
    /// either attaches before it or is refused after it.
    /// </summary>
    private readonly Lock _lifecycleGate = new();

    /// <summary>
    /// Set by <see cref="Dispose"/> under <see cref="_lifecycleGate"/>. A start that finds it set throws
    /// <see cref="ObjectDisposedException"/> and attaches nothing; a stop that finds it set does nothing.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Whether the manager is attached to the server now. Set by the first of <see cref="StartingAsync"/> and
    /// <see cref="StartAsync"/>, cleared by the stop's detach, read and written under <see cref="_lifecycleGate"/>.
    /// </summary>
    private bool _attached;

    /// <summary>
    /// Whether the attachment under <c>"default"</c> is the one this service's start installed, so the stop releases it.
    /// Set with <see cref="_attached"/> by a start whose attach returned, cleared by the stop, read and written under
    /// <see cref="_lifecycleGate"/>.
    /// </summary>
    private bool _owned;

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
        // that can be aborted. The token the manager saves under is SessionPersistenceTokenService's, not this one's.
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached)
                return;

            // The host installed "default" first when the engine held it for this server before the attach and this
            // service's own attach reached that engine; a held id whose engine the attach did not reach belongs to another
            // manager, and this service's attach of its own manager is its own. The probe sees only this call's attach of
            // this server under "default" to that engine, not another attach on it. A hand attach racing this start is
            // out of scope.
            var heldBefore = engine is not null && ReferenceEquals(engine.AttachedServer("default"), server);
            var reachedEngine = false;
            if (heldBefore)
            {
                using var probe = engine!.ProbeAttach(server, "default");
                sessionManager.AttachToServer(server, "default");
                reachedEngine = probe.Reached;
            }
            else
            {
                sessionManager.AttachToServer(server, "default");
            }

            _attached = true;
            _owned = !(heldBefore && reachedEngine);
        }
    }

    /// <summary>
    /// Detaches the manager when this service's start installed the attachment, so no server event can start a new save
    /// while the shutdown budget runs. An attachment the host installed before start stays; saves it starts after this
    /// stop still run under the persistence token, which the host's stop token cuts. The host's stop token is wired onto
    /// the persistence token by <see cref="SessionPersistenceTokenService"/>, after every hosted service's stop has
    /// returned. A stop after disposal does nothing.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return Task.CompletedTask;

            if (_owned)
                sessionManager.DetachFromServer("default");
            _attached = false;
            _owned = false;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
            _disposed = true;
    }
}
