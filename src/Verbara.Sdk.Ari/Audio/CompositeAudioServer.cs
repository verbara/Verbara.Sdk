using System.Reactive.Linq;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Aggregates multiple audio servers (AudioSocket + WebSocket) into a single IAudioServer view.
/// </summary>
/// <remarks>
/// <see cref="ActiveStreams"/> and <see cref="ActiveStreamCount"/> only merge: the list holds every
/// server's streams and the count is their sum. <see cref="GetStream(string)"/> hands one string to
/// every server in turn and returns the first stream any of them holds under it.
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
    /// <para>
    /// A <see langword="null"/> means that no aggregated server holds a live stream under
    /// <paramref name="channelId"/>. A found stream says its transport by its type: a WebSocket stream
    /// is an <see cref="IChanWebSocketSession"/>, an AudioSocket stream is not.
    /// </para>
    /// <para>
    /// Each server compares the string with its own keys, ordinally and as given: no server holds a
    /// notion of a key "of another form", so this method cannot tell a miss from a value meant for
    /// another transport, and does not try. <see cref="AudioSocketServer"/> keys on the canonical UUID
    /// of Asterisk's identification frame (the <c>data</c> of the create);
    /// <see cref="WebSocketAudioServer"/> on the literal <c>channel_id</c> of the connection's
    /// <c>MEDIA_START</c> (the ARI channel id of an <c>externalMedia</c> create), or the upgrade path's
    /// last segment for a connection that names no call. See
    /// <see cref="IAudioServer.GetStream(string)"/> for each key in full.
    /// </para>
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
