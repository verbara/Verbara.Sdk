using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Topics;
using Xunit;

namespace Verbara.Sdk.Push.Webhooks.IntegrationTests;

/// <summary>
/// Spec <c>webhook-event-routing</c>, requirement "The delivery service is subscribed when its start completes",
/// scenario "An event published as soon as the host has started", through a generic host.
/// </summary>
public sealed class WebhookHostStartupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HostStartAsync_ShouldReturnWithDeliverySubscribed_WhenWebhooksAreRegistered()
    {
        var requests = new CountSignal();
        SubscriptionRecordingBus? recording = null;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddVerbaraPush();
        builder.Services.AddVerbaraPushWebhooks(o =>
        {
            o.MaxRetries = 0;
            o.CircuitBreakerFailureThreshold = 0;
        });
        builder.Services.AddHttpClient(nameof(WebhookDeliveryService))
            .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(requests));
        builder.Services.AddSingleton<RxPushEventBus>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IPushEventBus>(
            sp => recording = new SubscriptionRecordingBus(sp.GetRequiredService<RxPushEventBus>())));
        using var host = builder.Build();
        await host.Services.GetRequiredService<IWebhookSubscriptionStore>().AddAsync(new WebhookSubscription
        {
            Id = "s-startup",
            TopicPattern = TopicPattern.Parse("startup.**"),
            TargetUrl = new Uri("http://127.0.0.1:9/hook"),
        });

        bool subscribedWhenStartReturned;
        bool delivered;
        await host.StartAsync();
        try
        {
            subscribedWhenStartReturned = recording!.IsSubscribed;
            await recording.PublishAsync(new HostStartupEvent
            {
                Metadata = new PushEventMetadata("t", null, DateTimeOffset.UtcNow, "e1", "startup.evt"),
            });
            try
            {
                await requests.WaitForAsync(1, Bound);
                delivered = true;
            }
            catch (TimeoutException)
            {
                delivered = false;
            }
        }
        finally
        {
            await host.StopAsync();
        }

        using var scope = new AssertionScope();
        subscribedWhenStartReturned.Should().BeTrue("the delivery service is subscribed by the time the host start returns");
        delivered.Should().BeTrue("an event published as soon as the host has started reaches its subscription");
    }

    private sealed record HostStartupEvent : PushEvent
    {
        public override string EventType => "startup.host";
    }

    private sealed class CountingHandler(CountSignal requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Increment();
            return Task.FromResult(Ok());
        }

        // The response a handler returns belongs to the HttpClient that sent the request, which disposes it.
        private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);
    }
}

/// <summary>An <see cref="IPushEventBus"/> decorator that records whether anything has subscribed through it.</summary>
internal sealed class SubscriptionRecordingBus(IPushEventBus inner) : IPushEventBus
{
    private int _subscribed;

    public bool IsSubscribed => Volatile.Read(ref _subscribed) != 0;

    public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
        where TEvent : PushEvent => inner.PublishAsync(pushEvent, ct);

    public IObservable<PushEvent> AsObservable() => new Recording<PushEvent>(inner.AsObservable(), this);

    public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => new Recording<TEvent>(inner.OfType<TEvent>(), this);

    private sealed class Recording<T>(IObservable<T> source, SubscriptionRecordingBus owner) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            var subscription = source.Subscribe(observer);
            Volatile.Write(ref owner._subscribed, 1);
            return subscription;
        }
    }
}
