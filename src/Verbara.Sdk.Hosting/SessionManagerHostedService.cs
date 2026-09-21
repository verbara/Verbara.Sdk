using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;

namespace Verbara.Sdk.Hosting;

internal sealed class SessionManagerHostedService(
    ICallSessionManager sessionManager,
    VerbaraServer server) : IHostedService, IDisposable
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
    /// Guards <see cref="Dispose"/>. <see cref="IDisposable"/> requires a second call to be ignored
    /// rather than throw, and <c>Cancel</c> on an already-released source throws
    /// <see cref="ObjectDisposedException"/> — so the release needs a gate, not just an ordering.
    /// </summary>
    private int _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The parameter is deliberately unused: it means the start was aborted, and this start
        // begins nothing that can be aborted. The manager gets the token this service owns.
        if (sessionManager is CallSessionManager csm)
        {
            csm.SetShutdownToken(_shutdown.Token);
            csm.AttachToServer(server, "default");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Detach first, so no server event can start a new save while the shutdown budget runs.
        if (sessionManager is CallSessionManager csm)
            csm.DetachFromServer("default");

        // The source is not cancelled here — a graceful stop is exactly the case in which an
        // in-flight save is still worth its budget. The registration cancels it when the host says
        // the shutdown is no longer graceful, and runs inline when that token arrives already
        // cancelled. Static callback with the source as state: no closure, no ExecutionContext.
        _stopRegistration.Dispose();
        _stopRegistration = cancellationToken.UnsafeRegister(
            static state => ((CancellationTokenSource)state!).Cancel(), _shutdown);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _stopRegistration.Dispose();
        // Cancel before releasing: a save that outlived the whole stop ends here, instead of being
        // left holding a token nothing could ever cancel.
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
