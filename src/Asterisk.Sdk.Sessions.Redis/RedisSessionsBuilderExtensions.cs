using Asterisk.Sdk.Sessions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Asterisk.Sdk.Sessions.Redis;

/// <summary>
/// Fluent <see cref="ISessionsBuilder"/> extensions for registering
/// <see cref="RedisSessionStore"/> as the active <see cref="SessionStoreBase"/>.
/// </summary>
public static class RedisSessionsBuilderExtensions
{
    /// <summary>
    /// Register <see cref="RedisSessionStore"/> as the active session backend. A singleton
    /// <see cref="IConnectionMultiplexer"/> is created from
    /// <see cref="RedisSessionStoreOptions.ConfigurationString"/> unless an
    /// <see cref="IConnectionMultiplexer"/> is already registered in the container.
    /// </summary>
    /// <param name="builder">The sessions builder.</param>
    /// <param name="configure">Optional callback to configure <see cref="RedisSessionStoreOptions"/>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown at resolution time when no <see cref="IConnectionMultiplexer"/> is registered and
    /// <see cref="RedisSessionStoreOptions.ConfigurationString"/> is null or empty.
    /// </exception>
    public static ISessionsBuilder UseRedis(
        this ISessionsBuilder builder,
        Action<RedisSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RedisSessionStoreOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.ConfigurationString))
            {
                throw new InvalidOperationException(
                    "RedisSessionStoreOptions.ConfigurationString must be set, or an " +
                    "IConnectionMultiplexer must be registered in DI before calling UseRedis().");
            }

            return ConnectionMultiplexer.Connect(opts.ConfigurationString);
        });

        RegisterStore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Register <see cref="RedisSessionStore"/> as the active session backend using a
    /// pre-built <see cref="IConnectionMultiplexer"/>. The multiplexer is added as a
    /// singleton if one is not already registered.
    /// </summary>
    /// <param name="builder">The sessions builder.</param>
    /// <param name="redis">An existing <see cref="IConnectionMultiplexer"/> instance.</param>
    /// <param name="configure">Optional callback to configure <see cref="RedisSessionStoreOptions"/>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static ISessionsBuilder UseRedis(
        this ISessionsBuilder builder,
        IConnectionMultiplexer redis,
        Action<RedisSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(redis);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.TryAddSingleton(redis);
        RegisterStore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Register <see cref="RedisSessionStore"/> as the active session backend using a
    /// connection string and optional per-option configuration. Convenience overload that
    /// delegates to <see cref="UseRedis(ISessionsBuilder, Action{RedisSessionStoreOptions}?)"/>.
    /// </summary>
    /// <param name="builder">The sessions builder.</param>
    /// <param name="configurationString">StackExchange.Redis configuration string.</param>
    /// <param name="configure">Optional callback to configure <see cref="RedisSessionStoreOptions"/>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static ISessionsBuilder UseRedis(
        this ISessionsBuilder builder,
        string configurationString,
        Action<RedisSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationString);

        return builder.UseRedis(opts =>
        {
            opts.ConfigurationString = configurationString;
            configure?.Invoke(opts);
        });
    }

    private static void RegisterStore(IServiceCollection services)
    {
        // Replace (not TryAdd) so we override the in-memory default that
        // AddAsteriskSessions() registers.
        services.Replace(ServiceDescriptor.Singleton<SessionStoreBase, RedisSessionStore>());
        // Forward ISessionStore to the replaced SessionStoreBase so resolving either
        // returns the same singleton instance.
        services.Replace(ServiceDescriptor.Singleton<ISessionStore>(
            sp => sp.GetRequiredService<SessionStoreBase>()));
    }
}
