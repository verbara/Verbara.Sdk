using System.Reactive.Linq;

namespace Asterisk.Sdk.Ari.Audio;

/// <summary>
/// Aggregates multiple audio servers (AudioSocket + WebSocket) into a single IAudioServer view.
/// </summary>
public sealed class CompositeAudioServer : IAudioServer
{
    private readonly IAudioServer[] _servers;

    public CompositeAudioServer(IEnumerable<IAudioServer> servers)
    {
        _servers = servers.ToArray();
    }

    public IObservable<IAudioStream> OnStreamConnected =>
        _servers.Select(s => s.OnStreamConnected).Merge();

    public IAudioStream? GetStream(string channelId)
    {
        foreach (var server in _servers)
        {
            var stream = server.GetStream(channelId);
            if (stream is not null) return stream;
        }
        return null;
    }

    public IEnumerable<IAudioStream> ActiveStreams =>
        _servers.SelectMany(s => s.ActiveStreams);

    public int ActiveStreamCount =>
        _servers.Sum(s => s.ActiveStreamCount);
}
