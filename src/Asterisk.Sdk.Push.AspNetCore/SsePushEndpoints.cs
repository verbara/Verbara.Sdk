namespace Asterisk.Sdk.Push.AspNetCore;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Asterisk.Sdk.Push.Authz;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Topics;

/// <summary>
/// Extension methods to map the SSE push event stream endpoint onto an ASP.NET Core
/// <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class SsePushEndpoints
{
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

        // Parse and authorize topic patterns from ?topic= query params.
        var patterns = BuildAuthorizedPatterns(ctx.Request.Query, subscriber, authorizer);

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        await using var writer = new StreamWriter(ctx.Response.Body) { AutoFlush = true };

        // Heartbeat every 15 s to keep proxies and clients from timing out the connection.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(writer, heartbeatCts.Token);

        // Subscribe to the bus and stream filtered events as SSE.
        var streamCts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => streamCts.TrySetResult());

        using (bus.AsObservable().Subscribe(evt =>
        {
            if (!deliveryFilter.IsDeliverableToSubscriber(evt, subscriber))
                return;

            if (!MatchesAnyPattern(evt, patterns, userId))
                return;

            WriteEvent(writer, evt);
        }))
        {
            await streamCts.Task.ConfigureAwait(false);
        }

        await heartbeatCts.CancelAsync().ConfigureAwait(false);
        await heartbeatTask.ConfigureAwait(false);
    }

    private static List<TopicPattern> BuildAuthorizedPatterns(
        IQueryCollection query,
        SubscriberContext subscriber,
        ISubscriptionAuthorizer authorizer)
    {
        var patterns = new List<TopicPattern>();

        foreach (var topicStr in query["topic"])
        {
            if (string.IsNullOrWhiteSpace(topicStr))
                continue;

            TopicPattern pattern;
            try
            {
                pattern = TopicPattern.Parse(topicStr);
            }
            catch (ArgumentException)
            {
                // Invalid pattern — skip silently rather than rejecting the whole connection.
                continue;
            }

            var subscriberWithTopic = subscriber with { RequestedTopicPattern = topicStr };
            var authResult = authorizer.CanSubscribe(subscriberWithTopic, pattern);
            if (authResult.Allowed)
                patterns.Add(pattern);
        }

        if (patterns.Count == 0)
        {
            // No explicit topics requested (or all were denied) — subscribe to everything
            // the authorizer allows. The AllowAll default in the MIT SDK permits this.
            var catchAll = TopicPattern.Parse("**");
            var subscriberDefault = subscriber with { RequestedTopicPattern = "**" };
            var authResult = authorizer.CanSubscribe(subscriberDefault, catchAll);
            if (authResult.Allowed)
                patterns.Add(catchAll);
        }

        return patterns;
    }

    private static bool MatchesAnyPattern(PushEvent evt, List<TopicPattern> patterns, string? userId)
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

    private static void WriteEvent(StreamWriter writer, PushEvent evt)
    {
        var eventType = evt.Metadata?.TopicPath ?? evt.EventType;
        var data = JsonSerializer.Serialize(evt, SseJsonContext.Default.PushEvent);
        writer.Write($"event: {eventType}\ndata: {data}\n\n");
    }

    private static async Task RunHeartbeatAsync(StreamWriter writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                await writer.WriteAsync(": heartbeat\n\n".AsMemory(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — swallow.
        }
    }
}
