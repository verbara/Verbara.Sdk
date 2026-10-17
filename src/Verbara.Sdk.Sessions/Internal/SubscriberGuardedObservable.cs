using Verbara.Sdk.Sessions.Diagnostics;

namespace Verbara.Sdk.Sessions.Internal;

/// <summary>
/// A notification stream whose every subscriber is wrapped in its own guard: an exception a subscriber throws from
/// <c>OnNext</c>, <c>OnError</c> or <c>OnCompleted</c> is caught at that subscriber, counted on
/// <c>sessions.events.subscriber_faults</c> and handed to the owner's report, and the notification goes on to every
/// later subscriber. Nothing a subscriber throws reaches the code that raised the notification.
/// </summary>
/// <remarks>
/// <para>The guard sits between <paramref name="source"/> and the caller's observer, so it covers every raise on the
/// source, present and future, with no site to forget. Disposing the subscription <see cref="Subscribe"/> returns
/// removes the guard from the source.</para>
/// <para>A subscriber subscribed directly whose handler throws stays subscribed. One composed through an operator
/// (<c>Where</c>, <c>Select</c>, <c>OfType</c>, …) is ended by that operator on its first failure; the guard reports
/// that one failure and cannot keep the subscription alive. Only what throws on the delivering thread is caught.</para>
/// <para>The fault is counted before it is reported, and an exception the report itself throws (a logger whose
/// provider fails, for one) is dropped: it reaches neither the raiser nor the later subscribers.</para>
/// <para><see cref="OutOfMemoryException"/> is not caught.</para>
/// </remarks>
/// <param name="source">The stream the guards subscribe to.</param>
/// <param name="reportNext">Reports a failure of a subscriber's <c>OnNext</c>, with the notification it threw on.</param>
/// <param name="reportTerminal">
/// Reports a failure of a subscriber's <c>OnCompleted</c> (<c>"completion"</c>) or <c>OnError</c> (<c>"error"</c>).
/// </param>
internal sealed class SubscriberGuardedObservable<T>(
    IObservable<T> source,
    Action<Exception, T> reportNext,
    Action<Exception, string> reportTerminal) : IObservable<T>
{
    private readonly Action<Exception, T> _reportNext = reportNext;
    private readonly Action<Exception, string> _reportTerminal = reportTerminal;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return source.Subscribe(new Guard(this, observer));
    }

    private sealed class Guard(SubscriberGuardedObservable<T> owner, IObserver<T> inner) : IObserver<T>
    {
        /// <summary>Hands a subscriber's failure to the owner's report; what the report throws goes no further.</summary>
        private static void Report<TArg>(Action<Exception, TArg> report, Exception failure, TArg argument)
        {
            try
            {
                report(failure, argument);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The report is the only trace a subscriber's failure leaves; when it fails too, the fault stays
                // counted and the notification goes on to the later subscribers.
            }
        }

        public void OnNext(T value)
        {
            try
            {
                inner.OnNext(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SessionMetrics.SubscriberFaults.Add(1);
                Report(owner._reportNext, ex, value);
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
                SessionMetrics.SubscriberFaults.Add(1);
                Report(owner._reportTerminal, ex, "error");
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
                SessionMetrics.SubscriberFaults.Add(1);
                Report(owner._reportTerminal, ex, "completion");
            }
        }
    }
}
