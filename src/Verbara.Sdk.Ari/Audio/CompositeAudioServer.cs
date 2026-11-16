using System.Reactive.Disposables;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Aggregates multiple audio servers (AudioSocket + WebSocket) into a single IAudioServer view.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OnStreamConnected"/> keeps the contract of <see cref="IAudioServer.OnStreamConnected"/> as each
/// aggregated server keeps it: each subscription is made on every server's own announcement, and each stream
/// reaches the observer from the handler of the connection that carries it, with no lock and no catch in between. An observer may
/// therefore be invoked concurrently for different connections, on the same server or on different ones; one that
/// blocks holds only the connection it was handed; and one that throws loses that connection, as the server that
/// accepted it states, and stays subscribed for the later streams of every server.
/// </para>
/// <para>
/// <see cref="ActiveStreams"/> and <see cref="ActiveStreamCount"/> only merge: the list holds every
/// server's streams and the count is their sum. <see cref="GetStream(string)"/> hands one string to
/// every server in turn and returns the first stream any of them holds under it.
/// </para>
/// </remarks>
public sealed class CompositeAudioServer : IAudioServer
{
    private readonly IAudioServer[] _servers;
    private readonly Announcements _announcements;

    public CompositeAudioServer(IEnumerable<IAudioServer> servers)
    {
        _servers = servers.ToArray();
        _announcements = new Announcements(_servers);
    }

    /// <summary>
    /// Emits each new audio stream of every aggregated server, delivered as that server delivers it.
    /// </summary>
    /// <remarks>
    /// A subscriber is completed once, after every aggregated server has completed, and at once when there is no
    /// server. The first server that fails, or that cannot be subscribed to, ends the subscription: the subscriber
    /// receives that error once, is unsubscribed from the other servers and receives no later error or completion. No
    /// lock orders announcements against that error, so an announcement already being delivered when it arrives may
    /// still be running, or complete, after it. Disposing the subscription unsubscribes it from every server.
    /// </remarks>
    public IObservable<IAudioStream> OnStreamConnected => _announcements;

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

    /// <summary>
    /// Subscribes each subscriber to every server's own announcement, forwarding every notification as it arrives: no
    /// gate, no catch around forwarding, no safe-observer wrapper. That is what keeps the servers' contract, where a
    /// merge operator would serialize all connections behind one gate and end a subscription whose observer threw.
    /// </summary>
    private sealed class Announcements(IAudioServer[] servers) : IObservable<IAudioStream>
    {
        public IDisposable Subscribe(IObserver<IAudioStream> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            if (servers.Length == 0)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            var subscription = new Subscription(observer, servers.Length);
            try
            {
                for (var i = 0; i < servers.Length && !subscription.IsStopped; i++)
                    subscription.Attach(i, servers[i].OnStreamConnected.Subscribe(new Forwarder(subscription)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A server that cannot be subscribed to (one already disposed, say) fails the subscription as Merge
                // does: what was already attached is disposed and the observer receives the error.
                subscription.OnError(ex);
            }
            return subscription;
        }
    }

    /// <summary>One subscriber's subscriptions to every server, and the end of its sequence.</summary>
    private sealed class Subscription(IObserver<IAudioStream> observer, int serverCount) : IDisposable
    {
        private readonly IDisposable?[] _inner = new IDisposable?[serverCount];
        private int _running = serverCount;
        private int _stopped;

        public bool IsStopped => Volatile.Read(ref _stopped) != 0;

        public void Attach(int index, IDisposable inner)
        {
            Volatile.Write(ref _inner[index], inner);

            // A stop that walked the slots before this write could not see it: take it back and dispose it here.
            if (IsStopped)
                Interlocked.Exchange(ref _inner[index], null)?.Dispose();
        }

        public void OnNext(IAudioStream stream)
        {
            if (!IsStopped)
                observer.OnNext(stream);
        }

        public void OnError(Exception error)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            DisposeInner();
            observer.OnError(error);
        }

        public void OnCompleted()
        {
            if (Interlocked.Decrement(ref _running) != 0 || Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            DisposeInner();
            observer.OnCompleted();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _stopped, 1);
            DisposeInner();
        }

        private void DisposeInner()
        {
            for (var i = 0; i < _inner.Length; i++)
                Interlocked.Exchange(ref _inner[i], null)?.Dispose();
        }
    }

    /// <summary>One server's side of a subscription.</summary>
    private sealed class Forwarder(Subscription subscription) : IObserver<IAudioStream>
    {
        public void OnNext(IAudioStream value) => subscription.OnNext(value);

        public void OnError(Exception error) => subscription.OnError(error);

        public void OnCompleted() => subscription.OnCompleted();
    }
}
