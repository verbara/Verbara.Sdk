using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Push.Webhooks;

/// <summary>
/// Background service that subscribes to the Push bus and posts matching events as
/// HTTP webhooks to every registered <see cref="WebhookSubscription"/> with exponential
/// retry and HMAC-SHA256 signing.
/// </summary>
/// <remarks>
/// <para>
/// The service is subscribed to the bus by the time <see cref="StartAsync"/> completes, so an event
/// published once the host has started is dispatched to every subscription it matches. The bus serves its
/// current subscribers only: an event published before this service starts — by a hosted service registered
/// ahead of it, during that service's own start — is not delivered.
/// </para>
/// <para>
/// Delivery runs until the service is stopped or disposed: <see cref="StopAsync"/> ends the subscription
/// before it returns, and <see cref="Dispose"/> ends it even without a stop. The token passed to
/// <see cref="StartAsync"/> governs the start only; cancelling it after the start has completed does not stop
/// delivery. A service stopped and started again delivers again.
/// </para>
/// <para>
/// An event is matched by its <see cref="PushEventMetadata.TopicPath"/>; when that is null or empty, by its
/// <see cref="PushEvent.EventType"/>, the rule the SSE stream applies. An event type that is not a valid topic
/// reaches only subscriptions whose pattern is <c>**</c>. A non-empty topic path that is not a valid topic is
/// never matched by the event type instead: the event is delivered to no subscription and logged as a dispatch
/// failure.
/// </para>
/// </remarks>
public sealed partial class WebhookDeliveryService : BackgroundService
{
    private readonly IPushEventBus _bus;
    private readonly IWebhookSubscriptionStore _store;
    private readonly IWebhookSigner _signer;
    private readonly IWebhookPayloadSerializer _serializer;
    private readonly IHttpClientFactory _httpFactory;
    private readonly WebhookDeliveryOptions _options;
    private readonly WebhookMetrics _metrics;
    private readonly ILogger<WebhookDeliveryService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuits = new(StringComparer.Ordinal);
    private readonly Lock _lifetimeLock = new();

    // Owned per start: the subscription made by the last StartAsync and the source that cancels its dispatches.
    // Both are taken exactly once by StopAsync or Dispose, whichever comes first.
    private IDisposable? _subscription;
    private CancellationTokenSource? _dispatchCts;

    internal WebhookDeliveryService(
        IPushEventBus bus,
        IWebhookSubscriptionStore store,
        IWebhookSigner signer,
        IWebhookPayloadSerializer serializer,
        IHttpClientFactory httpFactory,
        IOptions<WebhookDeliveryOptions> options,
        WebhookMetrics metrics,
        ILogger<WebhookDeliveryService> logger,
        TimeProvider? timeProvider = null)
    {
        _bus = bus;
        _store = store;
        _signer = signer;
        _serializer = serializer;
        _httpFactory = httpFactory;
        ArgumentNullException.ThrowIfNull(options);
        WebhookDeliveryRule.ThrowIfUnusable(options.Value);
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Constructor used by DI.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A <see cref="WebhookDeliveryOptions"/> value webhook delivery cannot use; <c>ParamName</c> names the option.
    /// </exception>
    public WebhookDeliveryService(
        IPushEventBus bus,
        IWebhookSubscriptionStore store,
        IWebhookSigner signer,
        IHttpClientFactory httpFactory,
        IOptions<WebhookDeliveryOptions> options,
        ILoggerFactory loggerFactory)
        : this(
            bus,
            store,
            signer,
            new DefaultWebhookPayloadSerializer(),
            httpFactory,
            options,
            new WebhookMetrics(),
            loggerFactory.CreateLogger<WebhookDeliveryService>(),
            timeProvider: null)
    {
    }

    /// <summary>
    /// Subscribes to the Push bus, then starts the service. Delivery has begun when the returned task
    /// completes. A second start without a stop does not subscribe again.
    /// </summary>
    /// <param name="cancellationToken">Aborts the start; it does not govern delivery once the start has completed.</param>
    /// <exception cref="ObjectDisposedException">The Push bus has been disposed.</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeLock)
        {
            if (_subscription is null)
            {
                // A fresh source per start, neither linked to the start token nor kept with it: a stop cancels
                // it, and the next start must not inherit a cancelled one.
                var dispatchCts = new CancellationTokenSource();
                var token = dispatchCts.Token;
                try
                {
                    _subscription = _bus.AsObservable()
                        .Subscribe(
                            evt => _ = DispatchEventAsync(evt, token),
                            ex => LogObserverError(_logger, ex));
                }
                catch
                {
                    dispatchCts.Dispose();
                    throw;
                }

                _dispatchCts = dispatchCts;
            }
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Ends the bus subscription — nothing is dispatched once it returns — cancels the deliveries in flight,
    /// then stops the service.
    /// </summary>
    /// <param name="cancellationToken">Bounds how long the stop waits for the service to end.</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var (subscription, dispatchCts) = TakeLifetime();
        subscription?.Dispose();
        if (dispatchCts is not null)
        {
            await dispatchCts.CancelAsync().ConfigureAwait(false);
            dispatchCts.Dispose();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the bus subscription and cancels the deliveries in flight, whether or not the service was
    /// stopped first. A second call releases nothing again.
    /// </summary>
    public override void Dispose()
    {
        var (subscription, dispatchCts) = TakeLifetime();
        subscription?.Dispose();
        if (dispatchCts is not null)
        {
            dispatchCts.Cancel();
            dispatchCts.Dispose();
        }

        base.Dispose();
    }

    private (IDisposable? Subscription, CancellationTokenSource? DispatchCts) TakeLifetime()
    {
        lock (_lifetimeLock)
        {
            var taken = (_subscription, _dispatchCts);
            _subscription = null;
            _dispatchCts = null;
            return taken;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The subscription is made by StartAsync and released by StopAsync or Dispose; this only keeps the
        // service's execute task running until the host stops it.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // graceful shutdown
        }
    }

    // Fired without awaiting from the bus observer: it must never fault, or the failure would sit in an
    // unobserved task. Anything the dispatch throws (an unparsable TopicPath, a pattern that throws) is logged.
    private async Task DispatchEventAsync(PushEvent evt, CancellationToken ct)
    {
        try
        {
            await DispatchEventCoreAsync(evt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogDispatchFailed(_logger, ex, evt.EventType);
        }
    }

    private async Task DispatchEventCoreAsync(PushEvent evt, CancellationToken ct)
    {
        IReadOnlyList<WebhookSubscription> subs;
        try
        {
            subs = await _store.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnumerateFailed(_logger, ex);
            return;
        }

        // A non-empty topic path that does not parse throws here and is logged by DispatchEventAsync.
        var name = WebhookTopicResolver.Resolve(evt);
        var selfUserId = evt.Metadata?.UserId;
        foreach (var sub in subs)
        {
            if (!name.IsMatchedBy(sub.TopicPattern, selfUserId))
                continue;

            _ = DeliverAsync(sub, evt, ct);
        }
    }

    // Fired without awaiting from the dispatch: it must never fault. A failure outside the attempt loop's own
    // handling (the serializer, the circuit lookup) ends the delivery as a dead letter with one Error entry.
    private async Task DeliverAsync(WebhookSubscription sub, PushEvent evt, CancellationToken ct)
    {
        try
        {
            await DeliverCoreAsync(sub, evt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _metrics.DeadLetter.Add(1);
            LogDeliveryFailed(_logger, ex, sub.Id, evt.EventType);
        }
    }

    private async Task DeliverCoreAsync(WebhookSubscription sub, PushEvent evt, CancellationToken ct)
    {
        var body = _serializer.Serialize(evt);
        var maxRetries = sub.MaxRetries ?? _options.MaxRetries;

        var circuitEnabled = _options.CircuitBreakerFailureThreshold > 0;
        CircuitBreakerState? circuit = null;
        if (circuitEnabled)
        {
            var circuitKey = sub.TargetUrl.AbsoluteUri;
            circuit = _circuits.GetOrAdd(circuitKey, static key => new CircuitBreakerState(key));
            if (!circuit.ShouldAllow(_options.CircuitBreakerOpenDuration, _timeProvider))
            {
                _metrics.CircuitSkipped.Add(1);
                LogCircuitSkipped(_logger, sub.Id, circuitKey);
                return;
            }
        }

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
                return;

            try
            {
                using var client = _httpFactory.CreateClient(nameof(WebhookDeliveryService));
                client.Timeout = _options.TimeoutPerAttempt;

                using var request = new HttpRequestMessage(HttpMethod.Post, sub.TargetUrl);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                if (!string.IsNullOrEmpty(sub.Secret))
                {
                    var signature = _signer.Sign(body, sub.Secret);
                    request.Headers.TryAddWithoutValidation("X-Signature", signature);
                }
                request.Headers.TryAddWithoutValidation("X-Event-Type", evt.EventType);
                request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

                if (!string.IsNullOrEmpty(evt.Metadata?.TraceContext))
                    request.Headers.TryAddWithoutValidation("traceparent", evt.Metadata.TraceContext);

                if (sub.Headers is { } extraHeaders)
                    foreach (var kv in extraHeaders)
                        request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _metrics.DeliveriesSucceeded.Add(1);
                    circuit?.RecordSuccess();
                    return;
                }

                _metrics.DeliveriesFailed.Add(1);
                LogNon2xxResponse(_logger, sub.Id, (int)response.StatusCode, attempt + 1);
                RecordCircuitFailure(circuit, sub);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _metrics.DeliveriesFailed.Add(1);
                LogAttemptThrew(_logger, ex, sub.Id, attempt + 1);
                RecordCircuitFailure(circuit, sub);
            }

            if (attempt == maxRetries)
                break;

            _metrics.DeliveriesRetried.Add(1);
            // attempt is 0-based here, but BackoffSchedule is 1-based.
            // Next delay: delay BEFORE attempt (attempt+2), using the "current" iteration's back-off.
            // Attempt 0 just failed → now sleep delay for attempt 1 (= baseDelay). Hence attempt+1 is correct.
            // Computing or waiting the delay throws only on options the rule rejects at construction, i.e. after
            // they were changed on the shared options object. That delivery ends here as a dead letter with its own
            // Error entry; it does not fall through to LogRetriesExhausted, which would claim retries that never ran.
            try
            {
                var delay = Verbara.Sdk.Resilience.BackoffSchedule.Compute(
                    attempt + 1,
                    _options.InitialDelay,
                    multiplier: 2.0,
                    _options.MaxDelay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _metrics.DeadLetter.Add(1);
                LogBackoffFailed(_logger, ex, sub.Id, evt.EventType);
                return;
            }
        }

        _metrics.DeadLetter.Add(1);
        LogRetriesExhausted(_logger, sub.Id, maxRetries, evt.EventType);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Webhook delivery observer received an error")]
    private static partial void LogObserverError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to enumerate webhook subscriptions")]
    private static partial void LogEnumerateFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Webhook delivery {SubscriptionId} returned {StatusCode} on attempt {Attempt}")]
    private static partial void LogNon2xxResponse(ILogger logger, string subscriptionId, int statusCode, int attempt);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Webhook delivery {SubscriptionId} threw on attempt {Attempt}")]
    private static partial void LogAttemptThrew(ILogger logger, Exception ex, string subscriptionId, int attempt);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Webhook delivery {SubscriptionId} exhausted {Retries} retries for event {EventType}")]
    private static partial void LogRetriesExhausted(ILogger logger, string subscriptionId, int retries, string eventType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Webhook delivery {SubscriptionId} short-circuited (circuit open for {TargetUrl})")]
    private static partial void LogCircuitSkipped(ILogger logger, string subscriptionId, string targetUrl);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Webhook circuit opened for {TargetUrl} after {Threshold} consecutive failures")]
    private static partial void LogCircuitOpened(ILogger logger, string targetUrl, int threshold);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Webhook delivery {SubscriptionId} dead-lettered for event {EventType}: its retry delay could not be computed or waited")]
    private static partial void LogBackoffFailed(ILogger logger, Exception ex, string subscriptionId, string eventType);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Webhook dispatch failed for event {EventType}")]
    private static partial void LogDispatchFailed(ILogger logger, Exception ex, string eventType);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Webhook delivery {SubscriptionId} dead-lettered for event {EventType}: it failed outside its attempts")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception ex, string subscriptionId, string eventType);

    private void RecordCircuitFailure(CircuitBreakerState? circuit, WebhookSubscription sub)
    {
        if (circuit is null)
            return;

        var wasOpen = circuit.IsOpen;
        circuit.RecordFailure(_options.CircuitBreakerFailureThreshold, _timeProvider);
        if (!wasOpen && circuit.IsOpen)
        {
            _metrics.CircuitOpened.Add(1);
            LogCircuitOpened(_logger, sub.TargetUrl.AbsoluteUri, _options.CircuitBreakerFailureThreshold);
        }
    }
}
