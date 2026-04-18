# Asterisk.Sdk.Sessions.Redis

Redis-backed `SessionStoreBase` implementation for `Asterisk.Sdk.Sessions`. Enables horizontal scale-out of call-session state across multiple SDK hosts by persisting sessions to a shared Redis instance.

## Usage

```csharp
services.AddAsteriskSessionsBuilder()
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
services.AddAsteriskSessionsBuilder()
        .UseRedis(redis, opts => opts.KeyPrefix = "myapp:");
```

## Notes

- AOT-safe: uses source-generated JSON only.
- Active sessions stored as Redis `STRING` keyed by `{prefix}session:{sessionId}` with secondary
  index `{prefix}idx:linked:{linkedId}` and active set `{prefix}sessions:active`.
- Terminal sessions (`Completed`, `Failed`, `TimedOut`) are kept for `CompletedRetention` (default
  10 min) and tracked in the sorted set `{prefix}sessions:completed`.
