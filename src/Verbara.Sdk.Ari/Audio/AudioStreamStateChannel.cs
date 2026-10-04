using System.Diagnostics;
using System.Reactive.Subjects;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Ari.Audio;

internal static partial class AudioStreamStateLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "[Audio] A StateChanges observer threw; the other observers and the stream's teardown went on: channel_id={ChannelId}")]
    public static partial void ObserverFailed(ILogger logger, Exception exception, string channelId);
}

/// <summary>
/// The state notifications of one ARI audio stream: its one ending, the internal signal its server
/// waits on, and the guard that keeps one consumer observer's exception from reaching the others.
/// </summary>
/// <remarks>
/// <para>
/// <b>One ending.</b> Every party that can end a stream — the read pump on a hangup or error frame or
/// at its end, and the stream's disposal — calls <see cref="TryEnd"/>. The first call publishes the
/// ending (<see cref="AudioStreamState.Error"/> first when an error frame ended it, then
/// <see cref="AudioStreamState.Disconnected"/>); every later call publishes nothing. The disposal then
/// completes the sequence with <see cref="Dispose"/>.
/// </para>
/// <para>
/// <b>The server's signal.</b> <see cref="Ended"/> completes inside the first <see cref="TryEnd"/>,
/// before any observer is notified, so the server that owns the stream learns that it ended whatever
/// a consumer's observer does with the notification.
/// </para>
/// <para>
/// <b>The guard.</b> Each subscriber gets its own guarded observer. An exception it throws on a
/// notification the stream publishes is logged once at Error and delivery goes on to the next
/// subscriber. An exception it throws on the state the subject replays inside its own
/// <c>Subscribe</c> call is not a publication: it reaches the caller of that call, as it always did,
/// and the subscription it was made in is dropped first, so that observer receives nothing afterwards.
/// The replay is told apart by thread: the subject replays synchronously on the subscribing thread
/// while the wrapper's call into it is in progress, and a publication from the read pump arrives on
/// another thread.
/// </para>
/// </remarks>
internal sealed class AudioStreamStateChannel : IObservable<AudioStreamState>, IDisposable
{
    private readonly BehaviorSubject<AudioStreamState> _state;
    private readonly ILogger _logger;
    private readonly Func<string> _channelId;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _current;
    private int _endPublished;
    private int _completed;

    /// <summary>Initializes a channel whose subscribers are first replayed <paramref name="initial"/>.</summary>
    /// <param name="initial">The state a subscriber is replayed before anything is published.</param>
    /// <param name="logger">Where a subscriber's exception is reported.</param>
    /// <param name="channelId">The stream's id as it stands when a subscriber throws, for the log line.</param>
    public AudioStreamStateChannel(AudioStreamState initial, ILogger logger, Func<string> channelId)
    {
        _state = new BehaviorSubject<AudioStreamState>(initial);
        _current = (int)initial;
        _logger = logger;
        _channelId = channelId;
    }

    /// <summary>The last state published. Readable after the sequence completed, unlike the subject's own value.</summary>
    public AudioStreamState Value => (AudioStreamState)Volatile.Read(ref _current);

    /// <summary>Completes when the stream's ending is published, before any observer is notified of it.</summary>
    public Task Ended => _ended.Task;

    /// <summary>Whether the ending has been published.</summary>
    public bool HasEnded => Volatile.Read(ref _endPublished) != 0;

    /// <summary>Publishes <see cref="AudioStreamState.Connected"/>, unless the stream has already ended.</summary>
    public void PublishConnected()
    {
        if (HasEnded)
            return;

        Publish(AudioStreamState.Connected);
    }

    /// <summary>
    /// Publishes the stream's ending if no one has: <see cref="AudioStreamState.Error"/> first when
    /// <paramref name="error"/> is set, then <see cref="AudioStreamState.Disconnected"/>. Returns whether
    /// this call published it.
    /// </summary>
    public bool TryEnd(bool error)
    {
        if (Interlocked.Exchange(ref _endPublished, 1) != 0)
            return false;

        _ended.TrySetResult();
        if (error)
            Publish(AudioStreamState.Error);
        Publish(AudioStreamState.Disconnected);
        return true;
    }

    /// <summary>Completes the sequence and releases the subject. Every call after the first does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _state.OnCompleted();
        _state.Dispose();
    }

    public IDisposable Subscribe(IObserver<AudioStreamState> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new GuardedObserver(this, observer).SubscribeTo(_state);
    }

    private void Publish(AudioStreamState state)
    {
        Volatile.Write(ref _current, (int)state);
        _state.OnNext(state);
    }

    private void Report(Exception exception) =>
        AudioStreamStateLog.ObserverFailed(_logger, exception, _channelId());

    /// <summary>One subscriber, behind the guard.</summary>
    private sealed class GuardedObserver(AudioStreamStateChannel owner, IObserver<AudioStreamState> inner) : IObserver<AudioStreamState>
    {
        // The managed thread id of the subscribing call while it is inside the subject's Subscribe, 0
        // otherwise. A notification delivered on that thread in that window is the replay.
        private volatile int _replayThread;
        private ExceptionDispatchInfo? _replayFailure;

        public IDisposable SubscribeTo(BehaviorSubject<AudioStreamState> source)
        {
            _replayThread = Environment.CurrentManagedThreadId;
            IDisposable subscription;
            try
            {
                subscription = source.Subscribe(this);
            }
            finally
            {
                _replayThread = 0;
            }

            if (_replayFailure is null)
                return subscription;

            // The subject added this observer before it replayed, so without this the observer would
            // stay subscribed with no IDisposable ever handed back to its caller.
            subscription.Dispose();
            _replayFailure.Throw();
            throw new UnreachableException("ExceptionDispatchInfo.Throw does not return.");
        }

        public void OnNext(AudioStreamState value) => Deliver(() => inner.OnNext(value));

        public void OnCompleted() => Deliver(inner.OnCompleted);

        public void OnError(Exception error) => Deliver(() => inner.OnError(error));

        private void Deliver(Action deliver)
        {
            try
            {
                deliver();
            }
            catch (Exception ex) when (_replayThread == Environment.CurrentManagedThreadId)
            {
                // The replay inside the subscriber's own Subscribe call: handed back to that caller.
                _replayFailure = ExceptionDispatchInfo.Capture(ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A publication: consumer code failed, and the stream, its server and the other
                // subscribers go on. The line is the only trace of it.
                owner.Report(ex);
            }
        }
    }
}
