// Asterisk.Sdk - Sessions.Redis Example
// Demonstrates: pluggable session backend with Redis for multi-instance SDK deployments.
//
// Prereq: a running Redis (localhost:6379 by default). Quick start:
//     docker run --rm -p 6379:6379 redis:7-alpine
//
// In production the SDK's CallSessionManager populates + transitions sessions from AMI
// events automatically; this example constructs sessions manually to demonstrate the
// store round-trip. Run two copies simultaneously to confirm both see the same data.

using Asterisk.Sdk;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole());

var redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";

// 1. Register Sessions with the Redis backend — UseRedis overrides the in-memory default
//    registered by AddAsteriskSessionsBuilder.
services.AddAsteriskSessionsBuilder()
    .UseRedis(opts =>
    {
        opts.ConfigurationString = redisConn;
        opts.KeyPrefix = "ast:example:";
        opts.CompletedRetention = TimeSpan.FromMinutes(5);
    });

using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<ISessionStore>();
Console.WriteLine($"Resolved ISessionStore: {store.GetType().Name}");
Console.WriteLine($"Connected to Redis at {redisConn}");
Console.WriteLine();

// 2. Save three sample sessions. SetMetadata is the public surface for test/demo use;
//    in production the SDK fills the rest from channel events.
for (var i = 1; i <= 3; i++)
{
    var session = new CallSession(
        sessionId: $"demo-session-{i}",
        linkedId: $"demo-linked-{i}",
        serverId: "ast-01",
        direction: CallDirection.Inbound);
    session.SetMetadata("caller-number", $"+1555000{i:00}");
    session.SetMetadata("caller-name", $"Demo Caller {i}");
    session.SetMetadata("demo", "sessions-redis-example");

    await store.SaveAsync(session, default);
    Console.WriteLine($"Saved {session.SessionId} (caller-number={session.Metadata["caller-number"]}) state={session.State}");
}
Console.WriteLine();

// 3. Read everything back from Redis.
var active = await store.GetActiveAsync(default);
Console.WriteLine($"Active sessions in Redis: {active.Count()}");
foreach (var s in active)
{
    Console.WriteLine($"  - {s.SessionId}  caller={s.Metadata.GetValueOrDefault("caller-number")}  state={s.State}");
}
Console.WriteLine();

// 4. Look up by linked id — RedisSessionStore uses a secondary index for O(1) lookup.
var byLinked = await store.GetByLinkedIdAsync("demo-linked-2", default);
Console.WriteLine($"GetByLinkedId('demo-linked-2') -> {byLinked?.SessionId ?? "(null)"}");

// 5. Cleanup (so a re-run starts clean).
foreach (var s in active)
    await store.DeleteAsync(s.SessionId, default);
Console.WriteLine();
Console.WriteLine("Cleaned up. Done.");
