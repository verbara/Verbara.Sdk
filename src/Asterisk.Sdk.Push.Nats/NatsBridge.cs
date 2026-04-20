using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NATS.Client.Core;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Background service that bridges the in-process Push bus and a NATS subject hierarchy.
/// Publishes every local event to NATS, and — when <see cref="NatsBridgeOptions.Subscribe"/>
/// is configured — consumes the configured NATS subject filters and reinjects every
/// decoded message into the local bus as a <see cref="RemotePushEvent"/>. Connection is
/// established when the hosted service starts; a clean drain happens on stop.
/// </summary>
public sealed partial class NatsBridge : BackgroundService
{
    private readonly IPushEventBus _bus;
    private readonly NatsBridgeOptions _options;
    private readonly INatsPayloadSerializer _serializer;
    private readonly INatsPayloadDeserializer _deserializer;
    private readonly NatsMetrics _metrics;
    private readonly ILogger<NatsBridge> _logger;
    private readonly Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsPublisher>> _publisherFactory;
    private readonly Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsSubscriber>>? _subscriberFactory;

    private INatsPublisher? _publisher;
    private INatsSubscriber? _subscriber;
    private IDisposable? _subscription;

    internal NatsBridge(
        IPushEventBus bus,
        IOptions<NatsBridgeOptions> options,
        INatsPayloadSerializer serializer,
        INatsPayloadDeserializer deserializer,
        NatsMetrics metrics,
        ILogger<NatsBridge> logger,
        Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsPublisher>> publisherFactory,
        Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsSubscriber>>? subscriberFactory = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(deserializer);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(publisherFactory);

        _bus = bus;
        _options = options.Value;
        _serializer = serializer;
        _deserializer = deserializer;
        _metrics = metrics;
        _logger = logger;
        _publisherFactory = publisherFactory;
        _subscriberFactory = subscriberFactory;
    }

    /// <summary>
    /// DI constructor preserved for backwards compatibility with v1.12 consumers that
    /// resolve <c>NatsBridge</c> directly. Uses the default deserializer.
    /// </summary>
    public NatsBridge(
        IPushEventBus bus,
        IOptions<NatsBridgeOptions> options,
        INatsPayloadSerializer serializer,
        ILoggerFactory loggerFactory)
        : this(bus, options, serializer, new DefaultNatsPayloadDeserializer(), loggerFactory)
    {
    }

    /// <summary>DI constructor — builds a real <see cref="NatsConnection"/> on start.</summary>
    public NatsBridge(
        IPushEventBus bus,
        IOptions<NatsBridgeOptions> options,
        INatsPayloadSerializer serializer,
        INatsPayloadDeserializer deserializer,
        ILoggerFactory loggerFactory)
        : this(
            bus,
            options,
            serializer,
            deserializer,
            new NatsMetrics(),
            (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<NatsBridge>(),
            DefaultFactories(loggerFactory).publisher,
            DefaultFactories(loggerFactory).subscriber)
    {
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _publisher = await _publisherFactory(_options, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogConnectFailed(_logger, ex, _options.Url);
            throw;
        }

        LogConnected(_logger, _options.Url, _options.SubjectPrefix);

        _subscription = _bus.AsObservable()
            .Subscribe(
                evt => _ = DispatchAsync(evt, stoppingToken),
                ex => LogObserverError(_logger, ex));

        if (_options.Subscribe is { } subOpts && _subscriberFactory is not null)
        {
            await StartSubscribeLoopsAsync(subOpts, stoppingToken).ConfigureAwait(false);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // graceful shutdown
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_subscriber is { } s)
            {
                _subscriber = null;
                try { await s.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogDisposeFailed(_logger, ex); }
            }

            if (_publisher is { } p)
            {
                _publisher = null;
                try { await p.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogDisposeFailed(_logger, ex); }
            }
        }
    }

    private async Task DispatchAsync(PushEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        var publisher = _publisher;
        if (publisher is null) return;

        // Don't re-publish events that this bridge just reinjected from a remote peer —
        // they would bounce back to the same subscribers and, with non-matching source
        // ids, evade the skip-self guard on other nodes.
        if (evt is RemotePushEvent) return;

        string subject;
        byte[] payload;
        try
        {
            var topicPath = evt.Metadata?.TopicPath ?? evt.EventType;
            subject = NatsSubjectTranslator.ToNatsSubject(topicPath, _options.SubjectPrefix);
            payload = _serializer.Serialize(evt);
        }
        catch (Exception ex)
        {
            _metrics.EventsFailed.Add(1);
            LogSerializeFailed(_logger, ex, evt.EventType);
            return;
        }

        try
        {
            await publisher.PublishAsync(subject, payload, ct).ConfigureAwait(false);
            _metrics.EventsPublished.Add(1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown in progress
        }
        catch (Exception ex)
        {
            _metrics.EventsFailed.Add(1);
            LogPublishFailed(_logger, ex, subject, evt.EventType);
        }
    }

    private async Task StartSubscribeLoopsAsync(NatsSubscribeOptions subOpts, CancellationToken stoppingToken)
    {
        try
        {
            _subscriber = await _subscriberFactory!(_options, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSubscriberConnectFailed(_logger, ex, _options.Url);
            throw;
        }

        var filters = ResolveFilters(subOpts.SubjectFilters, _options.SubjectPrefix);
        foreach (var filter in filters)
        {
            var capturedFilter = filter;
            _ = Task.Run(() => ConsumeFromNatsAsync(capturedFilter, subOpts, stoppingToken), stoppingToken);
        }
    }

    private async Task ConsumeFromNatsAsync(string filter, NatsSubscribeOptions subOpts, CancellationToken ct)
    {
        var subscriber = _subscriber;
        if (subscriber is null) return;

        LogSubscribing(_logger, filter, subOpts.QueueGroup);

        try
        {
            await foreach (var msg in subscriber
                .SubscribeAsync(filter, subOpts.QueueGroup, ct)
                .ConfigureAwait(false))
            {
                HandleIncomingMessage(msg, subOpts, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            LogConsumeFailed(_logger, ex, filter);
        }
    }

    private void HandleIncomingMessage(NatsSubscriberMessage msg, NatsSubscribeOptions subOpts, CancellationToken ct)
    {
        RemotePushEvent? remote;
        try
        {
            remote = _deserializer.Deserialize(msg.Subject, msg.Payload);
        }
        catch (Exception ex)
        {
            _metrics.EventsDecodeFailed.Add(1);
            LogDecodeThrew(_logger, ex, msg.Subject);
            return;
        }

        if (remote is null)
        {
            _metrics.EventsDecodeFailed.Add(1);
            LogDecodeFailed(_logger, msg.Subject);
            return;
        }

        if (subOpts.SkipSelfOriginated
            && remote.SourceNodeId is { } sourceNodeId
            && !string.IsNullOrEmpty(_options.NodeId)
            && string.Equals(sourceNodeId, _options.NodeId, StringComparison.Ordinal))
        {
            _metrics.EventsSkipped.Add(1);
            return;
        }

        _ = PublishToLocalBusAsync(remote, msg.Subject, ct);
    }

    private async Task PublishToLocalBusAsync(RemotePushEvent remote, string subject, CancellationToken ct)
    {
        try
        {
            await _bus.PublishAsync(remote, ct).ConfigureAwait(false);
            _metrics.EventsReceived.Add(1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown in progress
        }
        catch (Exception ex)
        {
            LogLocalPublishFailed(_logger, ex, subject);
        }
    }

    private static string[] ResolveFilters(string[] configured, string subjectPrefix)
    {
        if (configured is null || configured.Length == 0)
        {
            return [$"{subjectPrefix}.>"];
        }
        return configured;
    }

    private static (Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsPublisher>> publisher,
        Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsSubscriber>> subscriber) DefaultFactories(
        ILoggerFactory loggerFactory)
    {
        // Share one NatsConnection across publisher + subscriber to halve the
        // connection count per bridge. The publisher owns the lifetime; the
        // subscriber's Dispose is a no-op unless it is the sole user.
        NatsConnection? shared = null;
        var gate = new SemaphoreSlim(1, 1);

        async ValueTask<NatsConnection> GetOrConnectAsync(NatsBridgeOptions opts, CancellationToken ct)
        {
            if (shared is { } existing) return existing;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (shared is { } raced) return raced;

                var natsOpts = NatsOpts.Default with
                {
                    Url = opts.Url,
                    Name = "Asterisk.Sdk.Push.Nats",
                    AuthOpts = NatsAuthOpts.Default with
                    {
                        Username = opts.Username ?? string.Empty,
                        Password = opts.Password ?? string.Empty,
                        Token = opts.Token ?? string.Empty,
                    },
                    LoggerFactory = loggerFactory,
                };

                var connection = new NatsConnection(natsOpts);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(opts.ConnectTimeoutSeconds));
                await connection.ConnectAsync().AsTask().WaitAsync(connectCts.Token).ConfigureAwait(false);
                shared = connection;
                return connection;
            }
            finally
            {
                gate.Release();
            }
        }

        Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsPublisher>> publisherFactory =
            async (opts, ct) => new NatsConnectionPublisher(await GetOrConnectAsync(opts, ct).ConfigureAwait(false));

        Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsSubscriber>> subscriberFactory =
            async (opts, ct) => new NatsConnectionSubscriber(
                await GetOrConnectAsync(opts, ct).ConfigureAwait(false),
                ownsConnection: false);

        return (publisherFactory, subscriberFactory);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "NATS bridge connected to {Url} (prefix={SubjectPrefix})")]
    private static partial void LogConnected(ILogger logger, string url, string subjectPrefix);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "NATS bridge failed to connect to {Url}")]
    private static partial void LogConnectFailed(ILogger logger, Exception ex, string url);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "NATS bridge observer received an error")]
    private static partial void LogObserverError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "NATS bridge failed to serialize event {EventType}")]
    private static partial void LogSerializeFailed(ILogger logger, Exception ex, string eventType);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "NATS bridge failed to publish {Subject} (eventType={EventType})")]
    private static partial void LogPublishFailed(ILogger logger, Exception ex, string subject, string eventType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "NATS bridge failed to dispose publisher cleanly")]
    private static partial void LogDisposeFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "NATS bridge subscribe loop failed to connect to {Url}")]
    private static partial void LogSubscriberConnectFailed(ILogger logger, Exception ex, string url);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "NATS bridge subscribing to {Filter} (queueGroup={QueueGroup})")]
    private static partial void LogSubscribing(ILogger logger, string filter, string? queueGroup);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "NATS bridge consume loop failed for filter {Filter}")]
    private static partial void LogConsumeFailed(ILogger logger, Exception ex, string filter);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "NATS bridge failed to decode payload on subject {Subject}")]
    private static partial void LogDecodeFailed(ILogger logger, string subject);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "NATS bridge deserializer threw on subject {Subject}")]
    private static partial void LogDecodeThrew(ILogger logger, Exception ex, string subject);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "NATS bridge failed to republish remote event to local bus (subject={Subject})")]
    private static partial void LogLocalPublishFailed(ILogger logger, Exception ex, string subject);
}
