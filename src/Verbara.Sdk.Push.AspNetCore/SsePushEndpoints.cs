namespace Verbara.Sdk.Push.AspNetCore;

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Push.Authz;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Delivery;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Topics;

/// <summary>
/// Extension methods to map the SSE push event stream endpoint onto an ASP.NET Core
/// <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class SsePushEndpoints
{
    /// <summary>The response header that lists, percent-encoded and comma-joined, the requested topics that were denied.</summary>
    internal const string DeniedTopicsHeader = "X-Push-Denied-Topics";

    /// <summary>The fixed 403 body: the authorizer's reason is logged, never returned.</summary>
    internal const string SubscriptionDeniedBody = "Subscription denied.";

    /// <summary>
    /// Maps the push event stream endpoint at <c>{prefix}/stream</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="prefix">URL prefix for all push endpoints. Defaults to <c>/api/v1/push</c>.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> for chaining.</returns>
    /// <remarks>
    /// Minimal API endpoint registration uses reflection-based parameter binding which is not
    /// fully AOT-compatible. Add <c>app.MapPushEndpoints()</c> to a native-AOT app only when
    /// <c>&lt;PublishAot&gt;true&lt;/PublishAot&gt;</c> is <em>not</em> set, or suppress the
    /// IL2026/IL3050 warnings explicitly with <c>[UnconditionalSuppressMessage]</c>.
    /// </remarks>
    [RequiresUnreferencedCode("Minimal API endpoint registration uses reflection for parameter binding.")]
    [RequiresDynamicCode("Minimal API endpoint registration requires dynamic code generation.")]
    public static IEndpointRouteBuilder MapPushEndpoints(
        this IEndpointRouteBuilder app,
        string prefix = "/api/v1/push")
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(prefix);

        group.MapGet("/stream", HandleSseStreamAsync)
            .WithName("PushStream")
            .WithDescription("SSE push event stream with topic filtering and tenant isolation.");

        return app;
    }

    [RequiresUnreferencedCode("Minimal API parameter binding uses reflection.")]
    [RequiresDynamicCode("Minimal API parameter binding requires dynamic code generation.")]
    private static async Task HandleSseStreamAsync(
        HttpContext ctx,
        IPushEventBus bus,
        ISubscriptionAuthorizer authorizer,
        IEventDeliveryFilter deliveryFilter,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var tenantId = ctx.User.FindFirst("tenantId")?.Value;
        var userId = ctx.User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(tenantId))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Missing tenantId claim.", ct).ConfigureAwait(false);
            return;
        }

        // Build the subscriber context for this connection.
        var subscriber = new SubscriberContext(
            TenantId: tenantId,
            UserId: userId,
            Roles: new HashSet<string>(StringComparer.Ordinal),
            Permissions: new HashSet<string>(StringComparer.Ordinal));

        // Admission is decided before the response starts (spec push-sse-admission): a refusal is written
        // as text/plain before any event-stream byte and before subscribing to the bus.
        var admission = SseAdmission.Decide(ctx.Request.Query["topic"], subscriber, authorizer);
        switch (admission.Outcome)
        {
            case SseAdmissionOutcome.InvalidTopic:
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "Invalid topic pattern: " + SseAdmission.EncodeTopics(admission.RefusedTopics), ct).ConfigureAwait(false);
                return;

            case SseAdmissionOutcome.Denied:
                SsePushLog.StreamRefused(
                    loggerFactory.CreateLogger(SsePushLog.Category),
                    tenantId,
                    userId,
                    SseAdmission.EncodeTopics(admission.RefusedTopics),
                    admission.FirstDenialReason);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(SubscriptionDeniedBody, ct).ConfigureAwait(false);
                return;

            case SseAdmissionOutcome.Admitted:
            default:
                break;
        }

        var patterns = admission.AllowedPatterns;
        if (admission.RefusedTopics.Count > 0)
            ctx.Response.Headers[DeniedTopicsHeader] = SseAdmission.EncodeTopics(admission.RefusedTopics);

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        // Read per request, never required: a host set up with AddVerbaraPush() only has no stream options
        // registered and is served with the defaults (spec push-sse-stream-delivery).
        var options = ctx.RequestServices.GetService<IOptions<SsePushStreamOptions>>()?.Value ?? SsePushStreamOptions.Default;
        var metrics = ctx.RequestServices.GetService<PushMetrics>();
        var logger = loggerFactory.CreateLogger(SsePushLog.Category);

        try
        {
            // StartAsync alone does not put the headers on the wire; the flush does (C7.md).
            await ctx.Response.StartAsync(ct).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
            LogStreamEnded(logger, tenantId, userId, ex);
            return;
        }

        // One writer per connection over a bounded queue of whole frames (design D3, D4): the bus callback and
        // the heartbeat only queue frames, so neither ever waits on the client and no two frames interleave.
        var queue = new SseFrameQueue(options.MaxQueuedBytesPerConnection, options.QueuedBytesObserved);
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteFramesAsync(ctx.Response.Body, queue, logger, tenantId, userId, streamCts.Token);
        var heartbeat = RunHeartbeatAsync(queue, options.HeartbeatInterval, streamCts.Token);
        try
        {
            using (bus.AsObservable().Subscribe(evt =>
            {
                if (!deliveryFilter.IsDeliverableToSubscriber(evt, subscriber))
                    return;

                if (!MatchesAnyPattern(evt, patterns, userId))
                    return;

                var queued = queue.EnqueueEvent(SseFrameFormat.Event(evt));
                if (queued.Dropped > 0)
                {
                    metrics?.SseEventsDropped.Add(queued.Dropped);
                    if (queued.EpisodeStarted)
                        SsePushLog.GapEpisodeStarted(logger, options.MaxQueuedBytesPerConnection, tenantId, userId);
                }
            }))
            {
                // The writer runs until the connection ends (abort, reset or cancellation).
                await writer.ConfigureAwait(false);
            }
        }
        finally
        {
            await streamCts.CancelAsync().ConfigureAwait(false);
            queue.Complete();
            await heartbeat.ConfigureAwait(false);
            await writer.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The connection's only writer: takes whole frames off the queue, writes a <c>.gap</c> first when frames
    /// were dropped, and flushes each frame. A disconnect, reset or cancellation ends it, logged at Debug.
    /// </summary>
    private static async Task WriteFramesAsync(
        Stream body, SseFrameQueue queue, ILogger logger, string tenantId, string? userId, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var next = await queue.DequeueAsync(ct).ConfigureAwait(false);
                if (next.Frame is null)
                    return;

                if (next.Dropped > 0)
                    await body.WriteAsync(SseFrameFormat.Gap(next.Dropped), ct).ConfigureAwait(false);
                await body.WriteAsync(next.Frame, ct).ConfigureAwait(false);
                await body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
            LogStreamEnded(logger, tenantId, userId, ex);
        }
    }

    /// <summary>
    /// Whether an exception is the connection ending: a cancellation (the request aborted) or the transport's
    /// reset (<see cref="IOException"/>, which Kestrel's <c>ConnectionResetException</c> derives from).
    /// </summary>
    private static void LogStreamEnded(ILogger logger, string tenantId, string? userId, Exception ex) =>
        SsePushLog.StreamEnded(logger, tenantId, userId, ex is OperationCanceledException ? "cancelled" : "reset");

    private static bool IsConnectionEnd(Exception ex) => ex is OperationCanceledException or IOException;

    private static bool MatchesAnyPattern(PushEvent evt, IReadOnlyList<TopicPattern> patterns, string? userId)
    {
        if (patterns.Count == 0)
            return false;

        var topicPath = evt.Metadata?.TopicPath;
        if (topicPath is null)
            return true; // No topic path — let delivery filter decide; pass through.

        TopicName topicName;
        try
        {
            topicName = TopicName.Parse(topicPath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Matches(topicName, userId))
                return true;
        }

        return false;
    }

    private static async Task RunHeartbeatAsync(SseFrameQueue queue, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                queue.TryEnqueueHeartbeat(SseFrameFormat.Heartbeat);
        }
        catch (OperationCanceledException)
        {
            // The stream ended — normal shutdown.
        }
    }
}
