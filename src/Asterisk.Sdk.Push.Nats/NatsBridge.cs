using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NATS.Client.Core;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Background service that subscribes to the Push bus and republishes every event to
/// a NATS subject derived from the event's <c>TopicPath</c>. Connection is established
/// when the hosted service starts; a clean drain happens on stop.
/// </summary>
public sealed partial class NatsBridge : BackgroundService
{
    private readonly IPushEventBus _bus;
    private readonly NatsBridgeOptions _options;
    private readonly INatsPayloadSerializer _serializer;
    private readonly NatsMetrics _metrics;
    private readonly ILogger<NatsBridge> _logger;
    private readonly Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsPublisher>> _publisherFactory;

    private INatsPublisher? _publisher;
    private IDisposable? _subscription;

    internal NatsBridge(
        IPushEventBus bus,
        IOptions<NatsBridgeOptions> options,
        INatsPayloadSerializer serializer,
        NatsMetrics metrics,
        ILogger<NatsBridge> logger,
        Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsPublisher>> publisherFactory)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(publisherFactory);

        _bus = bus;
        _options = options.Value;
        _serializer = serializer;
        _metrics = metrics;
        _logger = logger;
        _publisherFactory = publisherFactory;
    }

    /// <summary>DI constructor — builds a real <see cref="NatsConnection"/> on start.</summary>
    public NatsBridge(
        IPushEventBus bus,
        IOptions<NatsBridgeOptions> options,
        INatsPayloadSerializer serializer,
        ILoggerFactory loggerFactory)
        : this(
            bus,
            options,
            serializer,
            new NatsMetrics(),
            (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<NatsBridge>(),
            DefaultPublisherFactory(loggerFactory))
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

    private static Func<NatsBridgeOptions, CancellationToken, ValueTask<INatsPublisher>> DefaultPublisherFactory(
        ILoggerFactory loggerFactory)
    {
        return async (opts, ct) =>
        {
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
            // NATS v2 ConnectAsync returns ValueTask. Convert to Task so WaitAsync(ct) is available.
            await connection.ConnectAsync().AsTask().WaitAsync(connectCts.Token).ConfigureAwait(false);
            return new NatsConnectionPublisher(connection);
        };
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
}
