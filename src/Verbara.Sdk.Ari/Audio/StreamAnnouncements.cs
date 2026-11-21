using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Diagnostics;

namespace Verbara.Sdk.Ari.Audio;

internal static partial class StreamAnnouncementsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Server} OnStreamConnected observer failed; the connection and the other observers go on: channel_id={ChannelId}")]
    public static partial void StreamConnectedObserverFailed(ILogger logger, Exception exception, string server, string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Server} OnStreamConnected observer failed on the completion; the disposal and the other observers go on")]
    public static partial void StreamConnectedObserverFailedOnCompletion(ILogger logger, Exception exception, string server);
}

/// <summary>
/// One audio server's <c>OnStreamConnected</c>: a subject whose every subscriber stands behind its own guard. What an
/// observer throws on the delivering thread, on a stream or on the completion the server's disposal raises, is counted
/// once on <c>audio.stream_connected.subscriber_faults</c> with the server's transport, then logged once at Warning,
/// and goes no further: the announced connection stays open and registered, the later observers receive the stream,
/// and the disposal finishes. The observer that threw stays subscribed.
/// </summary>
/// <remarks>
/// <para>One guard per subscription, subscribed in the observer's place, so it covers every raise of the subject with
/// no site to forget; the composite, which forwards from each server's announcement, inherits it. A subscription
/// composed through an Rx operator is ended by that operator on its first failure; the guard reports that one failure
/// and cannot keep it alive.</para>
/// <para>The fault is counted before it is logged, and an exception the logger throws on the report (a host provider
/// that fails) is dropped. <see cref="OutOfMemoryException"/> is not caught.</para>
/// </remarks>
internal sealed class StreamAnnouncements : IObservable<IAudioStream>, IDisposable
{
    private readonly Subject<IAudioStream> _subject = new();
    private readonly ILogger _logger;
    private readonly string _server;
    private readonly KeyValuePair<string, object?> _transport;

    /// <param name="logger">The server's logger, so the line carries the server's category.</param>
    /// <param name="server">The server's log prefix, such as <c>[AudioSocket]</c>.</param>
    /// <param name="transport">The <c>audio.transport</c> tag of the server's measurements.</param>
    internal StreamAnnouncements(ILogger logger, string server, KeyValuePair<string, object?> transport)
    {
        _logger = logger;
        _server = server;
        _transport = transport;
    }

    public IDisposable Subscribe(IObserver<IAudioStream> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _subject.Subscribe(new GuardedObserver(this, observer));
    }

    /// <summary>Announces <paramref name="stream"/> to every observer, in subscription order.</summary>
    /// <exception cref="ObjectDisposedException">The server's disposal released the subject first.</exception>
    internal void Announce(IAudioStream stream) => _subject.OnNext(stream);

    /// <summary>Completes every observer: the server's disposal.</summary>
    internal void Complete() => _subject.OnCompleted();

    public void Dispose() => _subject.Dispose();

    // One failure of an observer: counted once, then written once at Warning. A null stream is the completion.
    private void Report(Exception failure, IAudioStream? stream)
    {
        AudioStreamMetrics.StreamConnectedSubscriberFaults.Add(1, _transport);
        try
        {
            if (stream is null)
                StreamAnnouncementsLog.StreamConnectedObserverFailedOnCompletion(_logger, failure, _server);
            else
                StreamAnnouncementsLog.StreamConnectedObserverFailed(_logger, failure, _server, stream.ChannelId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The host's logger failed on the report: the fault stays counted, and the connection, a disposal and the
            // other observers go on.
        }
    }

    /// <summary>One observer, behind the guard. A <c>Subject</c> replays nothing, so every notification is a publication.</summary>
    private sealed class GuardedObserver(StreamAnnouncements owner, IObserver<IAudioStream> inner) : IObserver<IAudioStream>
    {
        public void OnNext(IAudioStream value)
        {
            try
            {
                inner.OnNext(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Consumer code failed on the stream: the line and the count are the only trace of it.
                owner.Report(ex, value);
            }
        }

        public void OnCompleted()
        {
            try
            {
                inner.OnCompleted();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Raised by the disposal, which goes on to release the subject.
                owner.Report(ex, stream: null);
            }
        }

        public void OnError(Exception error)
        {
            try
            {
                inner.OnError(error);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The servers never fail the sequence; guarded the same way should that change.
                owner.Report(ex, stream: null);
            }
        }
    }
}
