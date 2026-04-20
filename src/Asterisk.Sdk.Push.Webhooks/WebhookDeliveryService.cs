using System.Net.Http.Headers;
using System.Reactive.Linq;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Topics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Background service that subscribes to the Push bus and posts matching events as
/// HTTP webhooks to every registered <see cref="WebhookSubscription"/> with exponential
/// retry and HMAC-SHA256 signing.
/// </summary>
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

    internal WebhookDeliveryService(
        IPushEventBus bus,
        IWebhookSubscriptionStore store,
        IWebhookSigner signer,
        IWebhookPayloadSerializer serializer,
        IHttpClientFactory httpFactory,
        IOptions<WebhookDeliveryOptions> options,
        WebhookMetrics metrics,
        ILogger<WebhookDeliveryService> logger)
    {
        _bus = bus;
        _store = store;
        _signer = signer;
        _serializer = serializer;
        _httpFactory = httpFactory;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Constructor used by DI.</summary>
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
            loggerFactory.CreateLogger<WebhookDeliveryService>())
    {
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _bus.AsObservable()
            .Subscribe(
                evt => _ = DispatchEventAsync(evt, stoppingToken),
                ex => LogObserverError(_logger, ex));

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task DispatchEventAsync(PushEvent evt, CancellationToken ct)
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

        var topic = ResolveTopic(evt);
        if (topic is null)
            return;

        var resolved = topic.Value;
        foreach (var sub in subs)
        {
            if (!sub.TopicPattern.Matches(resolved, selfUserId: evt.Metadata?.UserId))
                continue;

            _ = DeliverAsync(sub, evt, ct);
        }
    }

    private static TopicName? ResolveTopic(PushEvent evt)
    {
        var path = evt.Metadata?.TopicPath;
        if (string.IsNullOrEmpty(path))
            return null;
        return TopicName.Parse(path);
    }

    private async Task DeliverAsync(WebhookSubscription sub, PushEvent evt, CancellationToken ct)
    {
        var body = _serializer.Serialize(evt);
        var maxRetries = sub.MaxRetries ?? _options.MaxRetries;

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
                    return;
                }

                _metrics.DeliveriesFailed.Add(1);
                LogNon2xxResponse(_logger, sub.Id, (int)response.StatusCode, attempt + 1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _metrics.DeliveriesFailed.Add(1);
                LogAttemptThrew(_logger, ex, sub.Id, attempt + 1);
            }

            if (attempt == maxRetries)
                break;

            _metrics.DeliveriesRetried.Add(1);
            // attempt is 0-based here, but BackoffSchedule is 1-based.
            // Next delay: delay BEFORE attempt (attempt+2), using the "current" iteration's back-off.
            // Attempt 0 just failed → now sleep delay for attempt 1 (= baseDelay). Hence attempt+1 is correct.
            var delay = Asterisk.Sdk.Resilience.BackoffSchedule.Compute(
                attempt + 1,
                _options.InitialDelay,
                multiplier: 2.0,
                _options.MaxDelay);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
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
}
