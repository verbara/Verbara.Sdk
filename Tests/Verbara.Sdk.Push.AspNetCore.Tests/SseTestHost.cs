namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Verbara.Sdk.Push.Diagnostics;

/// <summary>How the test host registers the push services before mapping the endpoint.</summary>
public enum PushRegistration
{
    /// <summary><c>AddVerbaraPushAspNetCore</c>, the package's own registration.</summary>
    AspNetCore,

    /// <summary><c>AddVerbaraPush()</c> only, as the commercial push package's README sets hosts up.</summary>
    PushOnly,
}

/// <summary>How the test host decides who a stream request is.</summary>
public enum SseIdentityMode
{
    /// <summary>
    /// A middleware sets the principal: the one a test registered for the request through
    /// <see cref="SseTestHost.OpenStreamAsync(string, ClaimsPrincipal, CancellationToken)"/>, else the fixed principal
    /// <c>tenantId=T1</c>, <c>sub=u1</c>.
    /// </summary>
    Principal,

    /// <summary>
    /// Real <c>JwtBearer</c> authentication: the principal is whatever the handler builds from a token the host
    /// minted (<see cref="SseTestHost.MintToken"/>); no middleware sets one.
    /// </summary>
    JwtBearer,
}

/// <summary>A push event the SSE tests publish; its SSE name is its topic path.</summary>
public sealed record SseTestEvent(string Name) : PushEvent
{
    public override string EventType => "sse.test";
}

/// <summary>A push event whose <see cref="PushEvent.EventType"/> each test chooses per instance.</summary>
public sealed record TypedTestEvent(string Type) : PushEvent
{
    public override string EventType => Type;
}

/// <summary>An event whose type is the stream's reserved gap-marker name and which carries no topic path.</summary>
public sealed record ReservedNameEvent : PushEvent
{
    public override string EventType => ".gap";
}

/// <summary>
/// Authorizer that records every pattern it is asked about, and the subscriber it was handed with it, and denies
/// with an internal-looking reason.
/// </summary>
public sealed class RecordingAuthorizer : ISubscriptionAuthorizer
{
    /// <summary>The reason every denial carries; a 403 body must never contain it.</summary>
    public const string DenyReason = "internal rule 42: finance group only";

    private readonly Func<SubscriberContext, string, bool> _allow;
    private readonly ConcurrentQueue<string> _asked = new();
    private readonly ConcurrentQueue<SubscriberContext> _subscribers = new();

    /// <summary>Decides by the requested pattern alone.</summary>
    public RecordingAuthorizer(Func<string, bool> allow)
    {
        ArgumentNullException.ThrowIfNull(allow);
        _allow = (_, raw) => allow(raw);
    }

    /// <summary>Decides by the subscriber it is handed and the requested pattern.</summary>
    public RecordingAuthorizer(Func<SubscriberContext, string, bool> allow)
    {
        ArgumentNullException.ThrowIfNull(allow);
        _allow = allow;
    }

    /// <summary>Every pattern asked, in order, as <see cref="TopicPattern.ToString"/> renders it.</summary>
    public IReadOnlyList<string> Asked => [.. _asked];

    /// <summary>The subscriber handed with each ask, in the order of <see cref="Asked"/>.</summary>
    public IReadOnlyList<SubscriberContext> Subscribers => [.. _subscribers];

    public AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var raw = requestedPattern.ToString();
        _asked.Enqueue(raw);
        _subscribers.Enqueue(subscriber);
        return _allow(subscriber, raw) ? AuthorizationResult.Allow() : AuthorizationResult.Deny(DenyReason);
    }
}

/// <summary>
/// The default delivery filter, recording every subscriber it is handed; it decides exactly as
/// <see cref="DefaultDeliveryFilter"/> does.
/// </summary>
public sealed class RecordingDeliveryFilter : IEventDeliveryFilter
{
    private readonly DefaultDeliveryFilter _inner = new();
    private readonly ConcurrentQueue<SubscriberContext> _seen = new();

    /// <summary>Every subscriber handed so far, one entry per evaluated event and connection.</summary>
    public IReadOnlyList<SubscriberContext> Seen => [.. _seen];

    public bool IsDeliverableToSubscriber(PushEvent pushEvent, SubscriberContext subscriber)
    {
        _seen.Enqueue(subscriber);
        return _inner.IsDeliverableToSubscriber(pushEvent, subscriber);
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

/// <summary>An integer that tests wait on by value, never by time.</summary>
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
/// The bus the endpoint is handed, wrapped so a test can count live subscriptions (the
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
        // The stream's naming rule: a null or empty topic path is named by the event type.
        _names.Enqueue(string.IsNullOrEmpty(value.Metadata.TopicPath) ? value.EventType : value.Metadata.TopicPath);
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
/// The stream's settings a test needs (heartbeat interval, per-connection bound, the queue-depth hook), set
/// through <see cref="SsePushStreamOptions"/> — the bound through its public option, the heartbeat and the
/// hook through its internal members — and only when a test sets a value, so a host that sets nothing (the
/// <c>AddVerbaraPush()</c>-only host) reads the endpoint's defaults.
/// </summary>
public static class SseStreamSettings
{
    /// <summary>The bound the spec rules as the default: 1 MiB of UTF-8 frames per connection.</summary>
    public const long DefaultBoundBytes = 1_048_576;

    public static void Configure(IServiceCollection services, TimeSpan? heartbeatInterval, long? boundBytes, Action<long>? queuedBytesObserved = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (heartbeatInterval is null && boundBytes is null && queuedBytesObserved is null)
            return;

        services.Configure<SsePushStreamOptions>(o =>
        {
            if (heartbeatInterval is { } interval)
                o.HeartbeatInterval = interval;
            if (boundBytes is { } bound)
                o.MaxQueuedBytesPerConnection = bound;
            if (queuedBytesObserved is not null)
                o.QueuedBytesObserved = queuedBytesObserved;
        });
    }
}

/// <summary>The high-water mark of one host's per-connection queue, read through the stream's internal hook.</summary>
public sealed class QueueDepthProbe
{
    private long _max;
    private long _observations;

    /// <summary>The most bytes any connection of the host had queued at once.</summary>
    public long MaxQueuedBytes => Interlocked.Read(ref _max);

    /// <summary>How many times a frame was queued.</summary>
    public long Observations => Interlocked.Read(ref _observations);

    public void Observe(long queuedBytes)
    {
        Interlocked.Increment(ref _observations);
        var current = Interlocked.Read(ref _max);
        while (queuedBytes > current)
        {
            var seen = Interlocked.CompareExchange(ref _max, queuedBytes, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}

/// <summary>Options for <see cref="SseTestHost"/>.</summary>
public sealed record SseHostOptions
{
    public bool AllowSynchronousIO { get; init; }

    public PushRegistration Registration { get; init; } = PushRegistration.AspNetCore;

    /// <summary>The bus's <c>BufferCapacity</c>, pinned above the events a test publishes.</summary>
    public int BusCapacity { get; init; } = 64;

    public ISubscriptionAuthorizer? Authorizer { get; init; }

    public TimeSpan? HeartbeatInterval { get; init; }

    public long? PerConnectionBoundBytes { get; init; }

    /// <summary>When set, records the high-water mark of the connection's queue (the stream's internal hook).</summary>
    public QueueDepthProbe? QueueDepth { get; init; }

    /// <summary>How a request's principal is decided; the fixed-principal middleware by default.</summary>
    public SseIdentityMode Identity { get; init; } = SseIdentityMode.Principal;

    /// <summary>In <see cref="SseIdentityMode.JwtBearer"/> mode, the handler's <c>MapInboundClaims</c> (its default is true).</summary>
    public bool MapInboundClaims { get; init; } = true;

    /// <summary>When set, replaces the registered <see cref="IEventDeliveryFilter"/>.</summary>
    public IEventDeliveryFilter? DeliveryFilter { get; init; }

    /// <summary>When set, configures <see cref="SsePushStreamOptions"/> after the test's other stream settings.</summary>
    public Action<SsePushStreamOptions>? ConfigureStream { get; init; }
}

/// <summary>
/// A real Kestrel host on <c>127.0.0.1:0</c> (an IPv4 literal, never <c>localhost</c>) serving <c>MapPushEndpoints()</c>.
/// By default every request is a principal of tenant <c>T1</c>, user <c>u1</c>; a test may hand a principal per
/// request, or run the host on real <c>JwtBearer</c> authentication with tokens it mints.
/// </summary>
public sealed class SseTestHost : IAsyncDisposable
{
    public const string StreamPath = "/api/v1/push/stream";

    /// <summary>The test-only request header naming the principal a test registered for the request.</summary>
    public const string PrincipalHeader = "X-Test-Principal";

    private readonly ConcurrentDictionary<string, ClaimsPrincipal> _principals;
    private readonly JwtSettings _jwt;
    private readonly SseIdentityMode _identity;

    private SseTestHost(WebApplication app, CapturingLoggerProvider logs, HttpClient client, CountSignal completed, HostIdentity identity)
    {
        App = app;
        Logs = logs;
        Client = client;
        StreamRequestsCompleted = completed;
        Bus = app.Services.GetRequiredService<CountingPushEventBus>();
        BusDrops = new BusDropListener(app.Services.GetRequiredService<PushMetrics>());
        _principals = identity.Principals;
        _jwt = identity.Jwt;
        _identity = identity.Mode;
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

        var jwt = JwtSettings.Create();
        if (options.Identity == SseIdentityMode.JwtBearer)
        {
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o =>
                {
                    o.MapInboundClaims = options.MapInboundClaims;
                    o.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = jwt.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwt.Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = jwt.Key,
                        ValidateLifetime = true,
                    };
                });
        }

        if (options.Registration == PushRegistration.AspNetCore)
            builder.Services.AddVerbaraPushAspNetCore(o => o.BufferCapacity = options.BusCapacity);
        else
            builder.Services.AddVerbaraPush(o => o.BufferCapacity = options.BusCapacity);

        SseStreamSettings.Configure(builder.Services, options.HeartbeatInterval, options.PerConnectionBoundBytes, options.QueueDepth is { } probe ? probe.Observe : null);
        if (options.ConfigureStream is { } configureStream)
            builder.Services.Configure(configureStream);

        if (options.DeliveryFilter is { } deliveryFilter)
            builder.Services.AddSingleton(deliveryFilter);

        // The endpoint's bus, wrapped so the tests can count its subscriptions.
        builder.Services.AddSingleton<RxPushEventBus>();
        builder.Services.AddSingleton(sp => new CountingPushEventBus(sp.GetRequiredService<RxPushEventBus>()));
        builder.Services.AddSingleton<IPushEventBus>(sp => sp.GetRequiredService<CountingPushEventBus>());

        var app = builder.Build();
        var completed = new CountSignal();
        var principals = new ConcurrentDictionary<string, ClaimsPrincipal>(StringComparer.Ordinal);
        var fixedPrincipal = Principal(null, ("tenantId", "T1"), ("sub", "u1"));
        app.Use(async (ctx, next) =>
        {
            if (options.Identity == SseIdentityMode.Principal)
            {
                var id = ctx.Request.Headers[PrincipalHeader].ToString();
                ctx.User = id.Length == 0
                    ? fixedPrincipal
                    : principals.TryGetValue(id, out var registered) ? registered : new ClaimsPrincipal(new ClaimsIdentity());
            }

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
        if (options.Identity == SseIdentityMode.JwtBearer)
            app.UseAuthentication();
        app.MapPushEndpoints();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = Timeout.InfiniteTimeSpan };
        return new SseTestHost(app, logs, client, completed, new HostIdentity(options.Identity, principals, jwt));
    }

    /// <summary>
    /// A hand-built principal: one authenticated identity carrying <paramref name="claims"/>, whose
    /// <c>RoleClaimType</c> is <paramref name="roleType"/> (<see cref="ClaimTypes.Role"/> when null).
    /// </summary>
    public static ClaimsPrincipal Principal(string? roleType, params (string Type, string Value)[] claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(static c => new Claim(c.Type, c.Value)),
            "test",
            ClaimTypes.Name,
            roleType ?? ClaimTypes.Role));
    }

    /// <summary>Sends <c>GET /stream{query}</c> and returns when the response headers arrive.</summary>
    public Task<HttpResponseMessage> OpenStreamAsync(string query, CancellationToken ct) =>
        Client.GetAsync(new Uri(StreamPath + query, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, ct);

    /// <summary>
    /// Sends <c>GET /stream{query}</c> as <paramref name="principal"/> (the middleware hands it to this request only)
    /// and returns when the response headers arrive.
    /// </summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string query, ClaimsPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (_identity != SseIdentityMode.Principal)
            throw new InvalidOperationException("A hand-built principal needs the Principal identity mode.");

        var id = Guid.NewGuid().ToString("N");
        _principals[id] = principal;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(StreamPath + query, UriKind.Relative));
        request.Headers.Add(PrincipalHeader, id);
        return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// Sends <c>GET /stream{query}</c> with <c>Authorization: Bearer</c> and a token minted for this request from
    /// <paramref name="claims"/>, and returns when the response headers arrive.
    /// </summary>
    public async Task<HttpResponseMessage> OpenStreamWithTokenAsync(string query, IReadOnlyDictionary<string, object> claims, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(StreamPath + query, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(claims));
        return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// An HS256 token carrying <paramref name="claims"/>, signed with this host's per-run key and issued for its
    /// issuer and audience; valid for ten minutes.
    /// </summary>
    public string MintToken(IReadOnlyDictionary<string, object> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            Claims = claims.ToDictionary(static c => c.Key, static c => c.Value, StringComparer.Ordinal),
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(_jwt.Key, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public ValueTask PublishAsync(string topic, string tenant = "T1", string? user = null, string? correlationId = null) =>
        Bus.PublishAsync(new SseTestEvent(topic) { Metadata = new PushEventMetadata(tenant, user, DateTimeOffset.UtcNow, correlationId, topic) });

    /// <summary>Publishes an event of type <paramref name="eventType"/> with no topic path.</summary>
    public ValueTask PublishTopicLessAsync(string eventType, string tenant = "T1", string? user = null) =>
        Bus.PublishAsync(new TypedTestEvent(eventType) { Metadata = new PushEventMetadata(tenant, user, DateTimeOffset.UtcNow, null) });

    /// <summary>Publishes an event of type <paramref name="eventType"/> whose topic path is <paramref name="topicPath"/>, verbatim (empty or unparseable included).</summary>
    public ValueTask PublishTypedAsync(string eventType, string topicPath, string tenant = "T1", string? user = null) =>
        Bus.PublishAsync(new TypedTestEvent(eventType) { Metadata = new PushEventMetadata(tenant, user, DateTimeOffset.UtcNow, null, topicPath) });

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

/// <summary>The per-host signing key, issuer and audience of the tokens a <see cref="SseTestHost"/> accepts.</summary>
internal sealed record JwtSettings(SymmetricSecurityKey Key, string Issuer, string Audience)
{
    /// <summary>A fresh 32-byte random key (HS256's minimum) and a unique issuer and audience, generated per host.</summary>
    public static JwtSettings Create()
    {
        var unique = Guid.NewGuid().ToString("N");
        return new JwtSettings(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), "sse-test-issuer-" + unique, "sse-test-audience-" + unique);
    }
}

/// <summary>What a <see cref="SseTestHost"/> needs to decide who a request is.</summary>
internal sealed record HostIdentity(SseIdentityMode Mode, ConcurrentDictionary<string, ClaimsPrincipal> Principals, JwtSettings Jwt);

/// <summary>
/// One stream request: its response, a reader over its frames, and the token that aborts it, released together.
/// </summary>
public sealed class SseStreamClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _abort;
    private SseReader? _reader;

    private SseStreamClient(CancellationTokenSource abort, HttpResponseMessage? response)
    {
        _abort = abort;
        Response = response;
    }

    /// <summary>The response, or <see langword="null"/> when its headers did not arrive within the bound.</summary>
    public HttpResponseMessage? Response { get; }

    public HttpStatusCode? Status => Response?.StatusCode;

    /// <summary>Opens a stream request through <paramref name="open"/>; <paramref name="bound"/> is the failure bound on its headers.</summary>
    public static async Task<SseStreamClient> OpenAsync(Func<CancellationToken, Task<HttpResponseMessage>> open, TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(open);
        var abort = new CancellationTokenSource();
        try
        {
            return new SseStreamClient(abort, await open(abort.Token).WaitAsync(bound));
        }
        catch (TimeoutException)
        {
            return new SseStreamClient(abort, null);
        }
    }

    /// <summary>
    /// Reads frames until one is named <paramref name="sentinel"/> (the failure bound is <paramref name="bound"/>), and
    /// returns whether it arrived and the names of the event frames read before it in this call, heartbeats skipped.
    /// </summary>
    public async Task<(bool SawSentinel, IReadOnlyList<string> Names)> ReadUntilAsync(string sentinel, TimeSpan bound)
    {
        if (Response?.StatusCode != HttpStatusCode.OK)
            return (false, []);

        _reader ??= new SseReader(await Response.Content.ReadAsStreamAsync(_abort.Token));
        var start = _reader.Frames.Count;
        var saw = await _reader.ReadUntilAsync(f => string.Equals(f.EventName, sentinel, StringComparison.Ordinal), bound);
        var names = _reader.Frames.Skip(start)
            .Where(f => !f.IsHeartbeat && !string.Equals(f.EventName, sentinel, StringComparison.Ordinal))
            .Select(static f => f.EventName ?? f.ToString())
            .ToList();
        return (saw, names);
    }

    /// <summary>The whole body of a refused (non-stream) answer.</summary>
    public async Task<string> ReadBodyAsync(TimeSpan bound) =>
        Response is null ? string.Empty : await Response.Content.ReadAsStringAsync(_abort.Token).WaitAsync(bound);

    public async ValueTask DisposeAsync()
    {
        await _abort.CancelAsync();
        _reader?.Dispose();
        Response?.Dispose();
        _abort.Dispose();
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
        // fence-allow: SIMULATED-WORK — a slow client between reads, so the server's writes really go asynchronous
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
