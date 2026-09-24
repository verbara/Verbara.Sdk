using System.Reactive.Linq;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Aggregates multiple audio servers (AudioSocket + WebSocket) into a single IAudioServer view.
/// </summary>
/// <remarks>
/// <see cref="ActiveStreams"/> and <see cref="ActiveStreamCount"/> only merge, and are safe to read
/// as an aggregate. <see cref="GetStream(string)"/> is the member to read carefully: it hands one
/// string to every server in turn, and the servers do not agree on what that string means.
/// </remarks>
public sealed class CompositeAudioServer : IAudioServer
{
    private readonly IAudioServer[] _servers;

    public CompositeAudioServer(IEnumerable<IAudioServer> servers)
    {
        _servers = servers.ToArray();
    }

    public IObservable<IAudioStream> OnStreamConnected =>
        _servers.Select(s => s.OnStreamConnected).Merge();

    /// <summary>
    /// Try each aggregated server in construction order and return the first stream registered
    /// under <paramref name="channelId"/>.
    /// </summary>
    /// <remarks>
    /// <b>One string, two meanings.</b> The aggregated servers key their tables on different
    /// things: <see cref="AudioSocketServer"/> on the UUID Asterisk sends in its AudioSocket
    /// identification frame — canonical lowercase hyphenated, compared ordinally — and
    /// <see cref="WebSocketAudioServer"/> on the last path segment of the HTTP upgrade request URL.
    /// Nothing arranges for one identifier to mean the same thing to both, so a value meant for one
    /// server is simply a miss in the other, and this method reports a miss and a key in the wrong
    /// form the same way: <see langword="null"/>. A caller that knows which transport it uses gets
    /// a narrower answer from that server directly. See
    /// <see cref="IAudioServer.GetStream(string)"/> for the full description of each key.
    /// </remarks>
    public IAudioStream? GetStream(string channelId)
    {
        foreach (var server in _servers)
        {
            if (server.GetStream(channelId) is { } stream) return stream;
        }
        return null;
    }

    public IEnumerable<IAudioStream> ActiveStreams =>
        _servers.SelectMany(s => s.ActiveStreams);

    public int ActiveStreamCount =>
        _servers.Sum(s => s.ActiveStreamCount);
}
