namespace Verbara.Sdk.Ari.Outbound;

/// <summary>
/// The stream an ARI outbound listener or connection hands out in place of its subject: every <c>Subscribe</c> wraps
/// the caller's observer in an <see cref="OutboundObserverGuard{T}"/> and subscribes that guard to the subject.
/// </summary>
/// <remarks>
/// One guard per subscription covers every raise on the subject, present and future, with no site to forget. A
/// subscribe on a released subject throws as the subject does.
/// </remarks>
internal sealed class OutboundGuardedStream<T> : IObservable<T>
    where T : class
{
    private readonly IObservable<T> _source;
    private readonly Action<Exception, string, T?> _report;

    internal OutboundGuardedStream(IObservable<T> source, Action<Exception, string, T?> report)
    {
        _source = source;
        _report = report;
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _source.Subscribe(new OutboundObserverGuard<T>(observer, _report));
    }
}

/// <summary>
/// Stands between an outbound subject and one observer. What that observer throws from <c>OnNext</c>,
/// <c>OnCompleted</c> or <c>OnError</c> is caught here and handed to the owner's report, and the notification goes on
/// to the next observer. Nothing an observer throws reaches the publisher.
/// </summary>
/// <remarks>
/// <para>The report is given the failure, the notification it was thrown on (<c>completion</c> and <c>error</c> for the
/// stream's end) and the value, when there is one. The owner counts the fault, then logs it, and drops whatever its
/// logger throws.</para>
/// <para>An observer subscribed directly stays subscribed. One composed through an Rx operator is ended by that
/// operator on its first failure. Only what throws on the delivering thread is caught, and
/// <see cref="OutOfMemoryException"/> is not.</para>
/// </remarks>
internal sealed class OutboundObserverGuard<T> : IObserver<T>
    where T : class
{
    internal const string Completion = "completion";
    internal const string Error = "error";

    private readonly IObserver<T> _inner;
    private readonly Action<Exception, string, T?> _report;

    internal OutboundObserverGuard(IObserver<T> inner, Action<Exception, string, T?> report)
    {
        _inner = inner;
        _report = report;
    }

    public void OnNext(T value)
    {
        try
        {
            _inner.OnNext(value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _report(ex, string.Empty, value);
        }
    }

    public void OnError(Exception error)
    {
        try
        {
            _inner.OnError(error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _report(ex, Error, null);
        }
    }

    public void OnCompleted()
    {
        try
        {
            _inner.OnCompleted();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _report(ex, Completion, null);
        }
    }
}
