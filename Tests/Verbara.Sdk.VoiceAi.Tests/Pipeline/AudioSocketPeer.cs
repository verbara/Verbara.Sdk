using System.Collections.Concurrent;
using Verbara.Sdk.VoiceAi.AudioSocket;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

// The same two helpers live in Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketPeer.cs. The
// two suites share no test project, so a change to one belongs in the other.

/// <summary>
/// Connects a peer that plays Asterisk's part: an <see cref="AudioSocketClient"/> on the IPv4
/// loopback literal (ADR-0044: never <c>localhost</c>), at the port the server actually bound.
/// </summary>
internal static class AudioSocketPeer
{
    /// <summary>
    /// Connects to <paramref name="server"/> on <c>127.0.0.1:BoundPort</c> and sends the UUID frame
    /// that identifies <paramref name="channelId"/>. The caller owns the returned client.
    /// </summary>
    /// <remarks>
    /// Returns once the frame is flushed, not once the server has read it. A test that needs the
    /// session waits on a <see cref="SessionStartedProbe"/>.
    /// </remarks>
    public static async Task<AudioSocketClient> ConnectAsync(
        AudioSocketServer server, Guid channelId, CancellationToken ct = default)
    {
        var port = server.BoundPort;
        if (port == 0)
            throw new InvalidOperationException("The server has no bound port: start it before connecting a peer.");

        var client = new AudioSocketClient("127.0.0.1", port, channelId);
        var connected = false;
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            connected = true;
            return client;
        }
        finally
        {
            if (!connected)
                await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A delegate a test subscribes to <see cref="AudioSocketServer.OnSessionStarted"/>. It signals, per
/// channel, that the server has raised that channel's session.
/// </summary>
/// <remarks>
/// The server invokes its multicast delegate in subscription order and awaits only the last result.
/// A probe subscribed after the broker has started therefore fires only after the broker's delegate
/// has returned for the same session, and the broker starts its handler inline. So when
/// <see cref="Started"/> completes, the broker has already handed that session on, or it never will
/// (design D8). Each signal completes asynchronously, so no test continuation runs on the server's
/// connection task.
/// </remarks>
internal sealed class SessionStartedProbe
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AudioSocketSession>> _started = new();

    private SessionStartedProbe()
    {
    }

    /// <summary>
    /// Subscribes a new probe to <paramref name="server"/>. To observe a broker, call it after the
    /// broker's <c>StartAsync</c>, which is where the broker subscribes.
    /// </summary>
    public static SessionStartedProbe SubscribeTo(AudioSocketServer server)
    {
        var probe = new SessionStartedProbe();
        server.OnSessionStarted += probe.OnSessionStarted;
        return probe;
    }

    /// <summary>
    /// Completes with the session once the server has raised it for <paramref name="channelId"/>,
    /// whether that happened before or after this call. Bound it with <c>WaitAsync</c>.
    /// </summary>
    public Task<AudioSocketSession> Started(Guid channelId) => SignalFor(channelId).Task;

    private ValueTask OnSessionStarted(AudioSocketSession session)
    {
        SignalFor(session.ChannelId).TrySetResult(session);
        return ValueTask.CompletedTask;
    }

    private TaskCompletionSource<AudioSocketSession> SignalFor(Guid channelId) =>
        _started.GetOrAdd(
            channelId,
            static _ => new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously));
}
