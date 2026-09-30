using System.Net.Sockets;

namespace Verbara.Sdk.Tests.Shared.Sockets;

/// <summary>
/// A client around a socket an accept fixture built, that completes <see cref="Released"/> when its
/// owner disposes it, so a test can wait on the release instead of polling the socket. For the
/// in-process servers this is the handler's <c>using (client)</c>, the last thing it does.
/// </summary>
/// <remarks>Linked into each suite that needs it (<c>&lt;Compile Include=… Link=…&gt;</c>); one definition.</remarks>
internal sealed class ReleaseSignallingClient : TcpClient
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReleaseSignallingClient(Socket socket)
        : base(AddressFamily.InterNetwork)
    {
        var unused = Client;
        Client = socket;
        unused.Dispose();
    }

    public Task Released => _released.Task;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _released.TrySetResult();
    }
}
