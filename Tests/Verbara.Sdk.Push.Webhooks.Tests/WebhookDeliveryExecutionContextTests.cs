using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// The delivery service enumerates its subscriptions inside the observer's <c>OnNext</c> call, on that call's
/// execution context. A host that wraps each <c>OnNext</c> in an ambient scope (a tenant taken from the event, say)
/// relies on its subscription store seeing that scope; a queue, a thread hop or a yielding <c>await</c> before the
/// enumeration would lose it.
/// </summary>
[Collection(WebhookProcessWideGroup.Name)]
public sealed class WebhookDeliveryExecutionContextTests
{
    private static readonly AsyncLocal<string?> Ambient = new();

    [Fact]
    public async Task Dispatch_ShouldEnumerateSubscriptionsOnTheObserverCallsContext_WhenTheCallerSetsAnAmbientValue()
    {
        var seen = new ConcurrentQueue<string>();
        await using var run = GuardRun.Create(
            GuardRun.ValidOptions(), HttpStatusCode.OK, [GuardRun.Subscription("s-ctx", "ctx.**")],
            inner => new AmbientScopeBus(inner));
        run.Store.OnEnumerate = () => seen.Enqueue(Ambient.Value ?? "<none>");
        await run.StartServiceAsync(CancellationToken.None);

        await PublishAsync(run, "tenant-1");
        await PublishAsync(run, "tenant-2");
        var delivered = await run.Endpoint.To("s-ctx").WaitForAsync(2, GuardRun.Bound);

        using var scope = new AssertionScope();
        delivered.Should().BeTrue("both events are delivered");
        seen.Should().Equal(["tenant-1", "tenant-2"],
            "the store is enumerated inside each OnNext call and sees the value its caller set for that event");
    }

    private static ValueTask PublishAsync(GuardRun run, string tenant) => run.InnerBus.PublishAsync(new RoutedEvent("ctx.evt")
    {
        Metadata = new PushEventMetadata(tenant, null, DateTimeOffset.UtcNow, "c", "ctx.evt"),
    });

    /// <summary>Calls every observer's <c>OnNext</c> with <see cref="Ambient"/> set to the event's tenant.</summary>
    private sealed class AmbientScopeBus(IPushEventBus inner) : IPushEventBus
    {
        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
            where TEvent : PushEvent => inner.PublishAsync(pushEvent, ct);

        public IObservable<PushEvent> AsObservable() => new Scoped(inner.AsObservable());

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => inner.OfType<TEvent>();

        private sealed class Scoped(IObservable<PushEvent> source) : IObservable<PushEvent>
        {
            public IDisposable Subscribe(IObserver<PushEvent> observer) => source.Subscribe(new ScopedObserver(observer));
        }

        private sealed class ScopedObserver(IObserver<PushEvent> observer) : IObserver<PushEvent>
        {
            public void OnCompleted() => observer.OnCompleted();

            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(PushEvent value)
            {
                var previous = Ambient.Value;
                Ambient.Value = value.Metadata?.TenantId;
                try
                {
                    observer.OnNext(value);
                }
                finally
                {
                    Ambient.Value = previous;
                }
            }
        }
    }
}
