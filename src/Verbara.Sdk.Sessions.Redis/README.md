# Verbara.Sdk.Sessions.Redis

Redis-backed `SessionStoreBase` implementation for `Verbara.Sdk.Sessions`. Enables horizontal scale-out of call-session state across multiple SDK hosts by persisting sessions to a shared Redis instance.

## Usage

```csharp
services.AddVerbaraSessionsBuilder()
        .UseRedis(opts =>
        {
            opts.ConfigurationString = "localhost:6379";
            opts.KeyPrefix = "myapp:";
            opts.CompletedRetention = TimeSpan.FromMinutes(10);
        });
```

Or supply a pre-built `IConnectionMultiplexer` directly:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
services.AddVerbaraSessionsBuilder()
        .UseRedis(redis, opts => opts.KeyPrefix = "myapp:");
```

## Notes

- AOT-safe: uses source-generated JSON only.
- Active sessions stored as Redis `STRING` keyed by `{prefix}session:{sessionId}` with active set
  `{prefix}sessions:active` and a `linkedid` index `{prefix}idx:linked-by-server:{linkedId}`: a `HASH` with one field
  per server id, kept by a single-key Lua script, so removing or expiring one server's session never hides another
  server's. Requires Redis 5.0 or later.
- Two lookups by `linkedid`: `GetByLinkedIdAsync(serverId, linkedId, ct)` returns the named server's session;
  `GetByLinkedIdAsync(linkedId, ct)` answers only when exactly one server holds the id, and returns `null` when two or
  more do.
- The string index of 2.10.x and earlier, `{prefix}idx:linked:{linkedId}`, is not migrated: it is still read, never
  written again, and given its retention or removed only when the session it names ends or is deleted.
- Terminal sessions (`Completed`, `Failed`, `TimedOut`) are kept for `CompletedRetention` (default
  10 min) and tracked in the sorted set `{prefix}sessions:completed`.
