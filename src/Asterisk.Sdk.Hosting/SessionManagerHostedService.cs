using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

internal sealed class SessionManagerHostedService(
    ICallSessionManager sessionManager,
    AsteriskServer server) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (sessionManager is CallSessionManager csm)
            csm.AttachToServer(server, "default");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (sessionManager is CallSessionManager csm)
            csm.DetachFromServer("default");
        return Task.CompletedTask;
    }
}
