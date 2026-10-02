namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Push.Diagnostics;

/// <summary>How the test host registers the push services before mapping the endpoint.</summary>
public enum PushRegistration
{
    /// <summary><c>AddVerbaraPushAspNetCore</c>, the package's own registration.</summary>
    AspNetCore,

    /// <summary><c>AddVerbaraPush()</c> only, as the commercial push package's README sets hosts up.</summary>
    PushOnly,
}

/// <summary>A push event the SSE tests publish; its SSE name is its topic path.</summary>
public sealed record SseTestEvent(string Name) : PushEvent
{
    public override string EventType => "sse.test";
}

/// <summary>An event whose type is the stream's reserved gap-marker name and which carries no topic path.</summary>
public sealed record ReservedNameEvent : PushEvent
{
    public override string EventType => ".gap";
}

/// <summary>Authorizer that records every pattern it is asked about and denies with an internal-looking reason.</summary>
public sealed class RecordingAuthorizer(Func<string, bool> allow) : ISubscriptionAuthorizer
{
    /// <summary>The reason every denial carries; a 403 body must never contain it.</summary>
    public const string DenyReason = "internal rule 42: finance group only";

    private readonly ConcurrentQueue<string> _asked = new();

    /// <summary>Every pattern asked, in order, as <see cref="TopicPattern.ToString"/> renders it.</summary>
    public IReadOnlyList<string> Asked => [.. _asked];

    public AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern)
    {
        var raw = requestedPattern.ToString();
        _asked.Enqueue(raw);
        return allow(raw) ? AuthorizationResult.Allow() : AuthorizationResult.Deny(DenyReason);
    }
}

/// <summary>One log entry the host wrote.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>Captures every log entry of one host.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    /// <summary>A snapshot of the entries written so far.</summary>
    public IReadOnlyList<CapturedLog> Snapshot() => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
        // Nothing to release: the entries outlive the host on purpose.
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}

/// <summary>An integer that tests wait on by value, never by time (design D7).</summary>
public sealed class CountSignal
{
    private int _value;
    private TaskCompletionSource _changed = NewSource();

    public int Value => Volatile.Read(ref _value);

    public void Add(int delta)
    {
        Interlocked.Add(ref _value, delta);
        Interlocked.Exchange(ref _changed, NewSource()).TrySetResult();
    }

    /// <summary>
    /// Waits until <paramref name="predicate"/> holds for the value; <paramref name="bound"/> is only the
    /// failure bound. Returns <see langword="false"/> when the bound expired first.
    /// </summary>
    public async Task<bool> WaitUntilAsync(Func<int, bool> predicate, TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var cts = new CancellationTokenSource(bound);
        while (true)
        {
            var changed = Volatile.Read(ref _changed).Task;
            if (predicate(Value))
                return true;

            try
            {
                await changed.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return predicate(Value);
            }
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The bus the endpoint is handed, wrapped so a test can count live subscriptions (design D7: the
/// endpoint takes the bus as a DI-bound parameter, and <c>RxPushEventBus._observers</c> is private).
/// </summary>
public sealed class CountingPushEventBus(IPushEventBus inner) : IPushEventBus
{
    /// <summary>Live subscriptions made through <see cref="AsObservable"/>.</summary>
    public CountSignal Subscribers { get; } = new();

    public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
        where TEvent : PushEvent => inner.PublishAsync(pushEvent, ct);

    public IObservable<PushEvent> AsObservable() => new CountingObservable(this, inner.AsObservable());

    public IObservable<TEvent> OfType<TEvent>()
        where TEvent : PushEvent => inner.OfType<TEvent>();

    private sealed class CountingObservable(CountingPushEventBus owner, IObservable<PushEvent> source) : IObservable<PushEvent>
    {
        public IDisposable Subscribe(IObserver<PushEvent> observer)
        {
            var inner = source.Subscribe(observer);
            owner.Subscribers.Add(1);
            return new Subscription(owner, inner);
        }
    }

    private sealed class Subscription(CountingPushEventBus owner, IDisposable inner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            inner.Dispose();
            owner.Subscribers.Add(-1);
        }
    }
}

/// <summary>An in-process subscriber that counts what it receives and remembers the names.</summary>
public sealed class CountingObserver : IObserver<PushEvent>
{
    private readonly ConcurrentQueue<string> _names = new();

    public CountSignal Received { get; } = new();

    public IReadOnlyList<string> Names => [.. _names];

    public void OnCompleted()
    {
        // The bus completes observers when it stops; nothing to count.
    }

    public void OnError(Exception error)
    {
        // The bus never calls OnError; nothing to count.
    }

    public void OnNext(PushEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _names.Enqueue(value.Metadata.TopicPath ?? value.EventType);
        Received.Add(1);
    }
}

/// <summary>Sums one host's <c>asterisk.push.events.dropped</c> (the bus's own drops) by meter instance.</summary>
public sealed class BusDropListener : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _dropped;

    public BusDropListener(PushMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var target = metrics.EventsDropped;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, target))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _dropped, value));
        _listener.Start();
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// The stream's settings a test needs (heartbeat interval, per-connection bound). Unfixed code has no
/// seam for either: <see cref="Configure"/> registers nothing today, so a test runs at the shipped 15 s
/// heartbeat and an unbounded (2.6.1) stream. Task 2.2 adds the internal <c>SsePushStreamOptions</c> and
/// registers it here — the one place the tests set the stream's settings — and only when a test sets a
/// value, so a host that sets nothing (the <c>AddVerbaraPush()</c>-only host) reads the endpoint's defaults.
/// </summary>
public static class SseStreamSettings
{
    /// <summary>The bound the spec rules as the default: 1 MiB of UTF-8 frames per connection.</summary>
    public const long DefaultBoundBytes = 1_048_576;

    public static void Configure(IServiceCollection services, TimeSpan? heartbeatInterval, long? boundBytes)
    {
        ArgumentNullException.ThrowIfNull(services);

        // No seam on unfixed code (design D3): task 2.2 registers SsePushStreamOptions here.
        _ = heartbeatInterval;
        _ = boundBytes;
    }
}

/// <summary>Options for <see cref="SseTestHost"/>.</summary>
public sealed record SseHostOptions
{
    public bool AllowSynchronousIO { get; init; }

    public PushRegistration Registration { get; init; } = PushRegistration.AspNetCore;

    /// <summary>The bus's <c>BufferCapacity</c>, pinned above the events a test publishes (design D7).</summary>
    public int BusCapacity { get; init; } = 64;

    public ISubscriptionAuthorizer? Authorizer { get; init; }

    public TimeSpan? HeartbeatInterval { get; init; }

    public long? PerConnectionBoundBytes { get; init; }
}

/// <summary>
/// A real Kestrel host on <c>127.0.0.1:0</c> (design D7, ADR-0044) serving <c>MapPushEndpoints()</c> to a
/// principal of tenant <c>T1</c>, user <c>u1</c>.
/// </summary>
public sealed class SseTestHost : IAsyncDisposable
{
    public const string StreamPath = "/api/v1/push/stream";

    private SseTestHost(WebApplication app, CapturingLoggerProvider logs, HttpClient client, CountSignal completed)
    {
        App = app;
        Logs = logs;
        Client = client;
        StreamRequestsCompleted = completed;
        Bus = app.Services.GetRequiredService<CountingPushEventBus>();
        BusDrops = new BusDropListener(app.Services.GetRequiredService<PushMetrics>());
    }

    public WebApplication App { get; }

    public CapturingLoggerProvider Logs { get; }

    public HttpClient Client { get; }

    public CountingPushEventBus Bus { get; }

    public BusDropListener BusDrops { get; }

    /// <summary>Stream requests whose pipeline returned (the request completed on the server).</summary>
    public CountSignal StreamRequestsCompleted { get; }

    /// <summary>Log entries that carry an exception.</summary>
    public IReadOnlyList<CapturedLog> LoggedExceptions => [.. Logs.Snapshot().Where(static l => l.Exception is not null)];

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test host; MapPushEndpoints is not trimmed here.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Test host; MapPushEndpoints is not AOT-compiled here.")]
    public static async Task<SseTestHost> StartAsync(SseHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost
            .UseKestrel(k => k.AllowSynchronousIO = options.AllowSynchronousIO)
            .UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        var logs = new CapturingLoggerProvider();
        builder.Logging.AddProvider(logs);

        if (options.Authorizer is { } authorizer)
            builder.Services.AddSingleton(authorizer);

        if (options.Registration == PushRegistration.AspNetCore)
            builder.Services.AddVerbaraPushAspNetCore(o => o.BufferCapacity = options.BusCapacity);
        else
            builder.Services.AddVerbaraPush(o => o.BufferCapacity = options.BusCapacity);

        SseStreamSettings.Configure(builder.Services, options.HeartbeatInterval, options.PerConnectionBoundBytes);

        // The endpoint's bus, wrapped so the tests can count its subscriptions.
        builder.Services.AddSingleton<RxPushEventBus>();
        builder.Services.AddSingleton(sp => new CountingPushEventBus(sp.GetRequiredService<RxPushEventBus>()));
        builder.Services.AddSingleton<IPushEventBus>(sp => sp.GetRequiredService<CountingPushEventBus>());

        var app = builder.Build();
        var completed = new CountSignal();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenantId", "T1"), new Claim("sub", "u1")], "test"));
        app.Use(async (ctx, next) =>
        {
            ctx.User = principal;
            try
            {
                await next(ctx);
            }
            finally
            {
                if (ctx.Request.Path.StartsWithSegments(StreamPath, StringComparison.Ordinal))
                    completed.Add(1);
            }
        });
        app.MapPushEndpoints();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = Timeout.InfiniteTimeSpan };
        return new SseTestHost(app, logs, client, completed);
    }

    /// <summary>Sends <c>GET /stream{query}</c> and returns when the response headers arrive.</summary>
    public Task<HttpResponseMessage> OpenStreamAsync(string query, CancellationToken ct) =>
        Client.GetAsync(new Uri(StreamPath + query, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, ct);

    public ValueTask PublishAsync(string topic, string tenant = "T1", string? user = null, string? correlationId = null) =>
        Bus.PublishAsync(new SseTestEvent(topic) { Metadata = new PushEventMetadata(tenant, user, DateTimeOffset.UtcNow, correlationId, topic) });

    public async ValueTask DisposeAsync()
    {
        BusDrops.Dispose();
        Client.Dispose();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                await App.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // A stream the unfixed code left blocked must not hang the test run; the app is disposed below.
            }
        }

        await App.DisposeAsync();
    }
}

/// <summary>One SSE frame: the lines up to the blank line that ends it.</summary>
public sealed record SseFrame(IReadOnlyList<string> Lines)
{
    public bool IsHeartbeat => Lines.Count == 1 && string.Equals(Lines[0], ": heartbeat", StringComparison.Ordinal);

    public IReadOnlyList<string> EventLines => [.. Lines.Where(static l => l.StartsWith("event:", StringComparison.Ordinal))];

    public IReadOnlyList<string> DataLines => [.. Lines.Where(static l => l.StartsWith("data:", StringComparison.Ordinal))];

    /// <summary>The value of the single <c>event:</c> line, or <see langword="null"/>.</summary>
    public string? EventName => EventLines.Count == 1 ? EventLines[0]["event:".Length..].TrimStart(' ') : null;

    /// <summary>The value of the single <c>data:</c> line, or <see langword="null"/>.</summary>
    public string? Data => DataLines.Count == 1 ? DataLines[0]["data:".Length..].TrimStart(' ') : null;

    /// <summary>The frame's size on the wire as UTF-8: every line plus its LF, plus the blank line.</summary>
    public int Utf8Bytes => Lines.Sum(static l => Encoding.UTF8.GetByteCount(l) + 1) + 1;

    public override string ToString() => string.Join("\\n", Lines.Select(static l => l.Length > 120 ? l[..120] + "…" : l));
}

/// <summary>Reads SSE frames off a response stream.</summary>
public sealed class SseReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly List<SseFrame> _frames = [];
    private List<string> _current = [];

    public SseReader(Stream stream) => _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16384);

    public IReadOnlyList<SseFrame> Frames => _frames;

    /// <summary>
    /// Reads frames until <paramref name="stop"/> holds for one, the stream ends, or the failure bound
    /// expires. Returns <see langword="true"/> only when <paramref name="stop"/> was met.
    /// </summary>
    public async Task<bool> ReadUntilAsync(Func<SseFrame, bool> stop, TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(stop);
        using var cts = new CancellationTokenSource(bound);
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token);
                if (line is null)
                    return false;

                if (line.Length > 0)
                {
                    _current.Add(line);
                    continue;
                }

                if (_current.Count == 0)
                    continue;

                var frame = new SseFrame(_current);
                _current = [];
                _frames.Add(frame);
                if (stop(frame))
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>A read-only stream that hands out at most <c>chunk</c> bytes per read and pauses after each one.</summary>
public sealed class ThrottledReadStream(Stream inner, int chunk, TimeSpan pause) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
        // fence-allow: SIMULATED-WORK — a slow client between reads, so the server's writes really go asynchronous (C7.md control N)
        await Task.Delay(pause, cancellationToken);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
