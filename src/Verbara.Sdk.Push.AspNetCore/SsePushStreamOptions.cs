namespace Verbara.Sdk.Push.AspNetCore;

using Microsoft.Extensions.Options;

/// <summary>
/// Settings of the SSE push stream served by <see cref="SsePushEndpoints.MapPushEndpoints"/>. Configure them with
/// <c>services.Configure&lt;SsePushStreamOptions&gt;(o =&gt; …)</c>; <c>AddVerbaraPushAspNetCore</c> validates them
/// when the host starts. A host that never configures them gets the defaults.
/// </summary>
public sealed class SsePushStreamOptions
{
    /// <summary>The default of <see cref="MaxQueuedBytesPerConnection"/>: 1 MiB.</summary>
    internal const long DefaultMaxQueuedBytesPerConnection = 1_048_576;

    /// <summary>The settings a request uses when the host registered none.</summary>
    internal static readonly SsePushStreamOptions Default = new();

    /// <summary>
    /// The most bytes of SSE frames (UTF-8, as written on the wire) that one connection may have waiting to be
    /// written. Defaults to 1 MiB (1,048,576 bytes); must be &gt;= 1.
    /// </summary>
    /// <remarks>
    /// When an event would take a connection's queue past this bound, the oldest queued frames are dropped
    /// and, before its next event, the client receives one <c>event: .gap</c> whose data is
    /// <c>{"dropped":N}</c>, the number of event frames dropped. The bus and every other subscriber are never
    /// slowed by a slow connection. Heartbeats are not queued while the queue is at the bound, and are never
    /// counted in <c>N</c>. A single event larger than the bound is still delivered, alone: everything queued
    /// before it is dropped and counted. Each drop is counted on the <c>asterisk.push.sse.events.dropped</c>
    /// counter of the <c>Verbara.Sdk.Push</c> meter, and each run of drops up to its <c>.gap</c> is logged
    /// once at <c>Warning</c>.
    /// </remarks>
    public long MaxQueuedBytesPerConnection { get; set; } = DefaultMaxQueuedBytesPerConnection;

    /// <summary>How often an idle connection is sent <c>: heartbeat</c>. Internal: tests shorten it.</summary>
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Test-only observation hook: called with the bytes queued for the connection after every frame is queued.
    /// </summary>
    internal Action<long>? QueuedBytesObserved { get; set; }
}

/// <summary>AOT-safe validation of <see cref="SsePushStreamOptions"/> (no DataAnnotations at run time).</summary>
internal sealed class SsePushStreamOptionsValidator : IValidateOptions<SsePushStreamOptions>
{
    public ValidateOptionsResult Validate(string? name, SsePushStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxQueuedBytesPerConnection < 1)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(SsePushStreamOptions.MaxQueuedBytesPerConnection)} must be >= 1 (was {options.MaxQueuedBytesPerConnection}).");
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(SsePushStreamOptions.HeartbeatInterval)} must be > 0 (was {options.HeartbeatInterval}).");
        }

        return ValidateOptionsResult.Success;
    }
}
