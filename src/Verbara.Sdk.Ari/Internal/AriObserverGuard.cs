using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Diagnostics;

namespace Verbara.Sdk.Ari.Internal;

/// <summary>
/// Stands between an ARI client's event stream and one observer. What that observer throws from <c>OnNext</c>,
/// <c>OnCompleted</c> or <c>OnError</c> is caught here, counted on <c>ari.events.subscriber_faults</c> and logged once
/// at Warning, and the notification goes on to the next observer. Nothing an observer throws reaches the client.
/// </summary>
/// <remarks>
/// <para>One guard per subscription, subscribed in the observer's place, so it covers every raise on the stream,
/// present and future, with no site to forget. It holds no per-event state: each event costs one interface call and
/// one <c>try</c>.</para>
/// <para>An observer subscribed directly whose handler throws stays subscribed. One composed through an Rx operator is
/// ended by that operator on its first failure: the guard reports that one failure and cannot keep the subscription
/// alive. Only what throws on the delivering thread is caught.</para>
/// <para>The fault is counted before it is reported, and an exception the report itself throws (a logger whose provider
/// fails, for one) is dropped: it reaches neither the client nor the next observer.
/// <see cref="OutOfMemoryException"/> is not caught.</para>
/// </remarks>
internal sealed class AriObserverGuard : IObserver<AriEvent>
{
    private const string Completion = "completion";
    private const string Error = "error";

    private readonly IObserver<AriEvent> _inner;
    private readonly ILogger _logger;

    internal AriObserverGuard(IObserver<AriEvent> inner, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _logger = logger;
    }

    public void OnNext(AriEvent value)
    {
        try
        {
            _inner.OnNext(value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(ex, value.Type);
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
            Report(ex, Error);
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
            Report(ex, Completion);
        }
    }

    private void Report(Exception failure, string? eventType)
    {
        AriMetrics.SubscriberFaults.Add(1);
        try
        {
            AriClientLog.ObserverFault(_logger, failure, eventType);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The report is the only trace an observer's failure leaves; when it fails too, the fault stays counted
            // and the notification goes on to the next observer.
        }
    }
}
