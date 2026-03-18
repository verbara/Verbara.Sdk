namespace Asterisk.Sdk.Redis.Spike.Store;

public sealed class RedisSessionStoreOptions
{
    /// <summary>Key prefix for all Redis keys. Default: "ast:".</summary>
    public string KeyPrefix { get; set; } = "ast:";

    /// <summary>How long completed sessions remain in Redis. Default: 10 minutes.</summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Redis database index. Default: 0.</summary>
    public int DatabaseIndex { get; set; }
}
