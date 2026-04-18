using System.Text.RegularExpressions;
using Asterisk.Sdk.Sessions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Asterisk.Sdk.Sessions.Postgres;

/// <summary>
/// Fluent <see cref="ISessionsBuilder"/> extensions for registering
/// <see cref="PostgresSessionStore"/> as the active <see cref="SessionStoreBase"/>.
/// </summary>
public static partial class PostgresSessionsBuilderExtensions
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierRegex();

    /// <summary>
    /// Register <see cref="PostgresSessionStore"/> as the active session backend. A singleton
    /// <see cref="NpgsqlDataSource"/> is created from
    /// <see cref="PostgresSessionStoreOptions.ConnectionString"/> unless an
    /// <see cref="NpgsqlDataSource"/> is already registered in the container.
    /// </summary>
    /// <param name="builder">The sessions builder.</param>
    /// <param name="configure">Optional callback to configure <see cref="PostgresSessionStoreOptions"/>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown at resolution time when no <see cref="NpgsqlDataSource"/> is registered and
    /// <see cref="PostgresSessionStoreOptions.ConnectionString"/> is null or empty, or when
    /// <c>TableName</c>/<c>SchemaName</c> fail identifier validation.
    /// </exception>
    public static ISessionsBuilder UsePostgres(
        this ISessionsBuilder builder,
        Action<PostgresSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        // Validate identifier options at resolve time — they're embedded as SQL literals.
        builder.Services.AddOptions<PostgresSessionStoreOptions>()
            .Validate(ValidateIdentifiers, "PostgresSessionStoreOptions.TableName and SchemaName must match ^[A-Za-z_][A-Za-z0-9_]*$.");

        builder.Services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PostgresSessionStoreOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.ConnectionString))
            {
                throw new InvalidOperationException(
                    "PostgresSessionStoreOptions.ConnectionString must be set, or an " +
                    "NpgsqlDataSource must be registered in DI before calling UsePostgres().");
            }

            return NpgsqlDataSource.Create(opts.ConnectionString);
        });

        RegisterStore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Register <see cref="PostgresSessionStore"/> as the active session backend using a
    /// pre-built <see cref="NpgsqlDataSource"/>. The data source is added as a singleton
    /// if one is not already registered.
    /// </summary>
    /// <param name="builder">The sessions builder.</param>
    /// <param name="dataSource">An existing <see cref="NpgsqlDataSource"/> instance.</param>
    /// <param name="configure">Optional callback to configure <see cref="PostgresSessionStoreOptions"/>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static ISessionsBuilder UsePostgres(
        this ISessionsBuilder builder,
        NpgsqlDataSource dataSource,
        Action<PostgresSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.AddOptions<PostgresSessionStoreOptions>()
            .Validate(ValidateIdentifiers, "PostgresSessionStoreOptions.TableName and SchemaName must match ^[A-Za-z_][A-Za-z0-9_]*$.");

        builder.Services.TryAddSingleton(dataSource);
        RegisterStore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Register <see cref="PostgresSessionStore"/> as the active session backend using a
    /// connection string and optional per-option configuration. Convenience overload that
    /// delegates to <see cref="UsePostgres(ISessionsBuilder, Action{PostgresSessionStoreOptions}?)"/>.
    /// </summary>
    /// <param name="builder">The sessions builder.</param>
    /// <param name="connectionString">Npgsql connection string.</param>
    /// <param name="configure">Optional callback to configure <see cref="PostgresSessionStoreOptions"/>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static ISessionsBuilder UsePostgres(
        this ISessionsBuilder builder,
        string connectionString,
        Action<PostgresSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder.UsePostgres(opts =>
        {
            opts.ConnectionString = connectionString;
            configure?.Invoke(opts);
        });
    }

    private static bool ValidateIdentifiers(PostgresSessionStoreOptions opts) =>
        opts is not null
        && IdentifierRegex().IsMatch(opts.TableName)
        && IdentifierRegex().IsMatch(opts.SchemaName);

    private static void RegisterStore(IServiceCollection services)
    {
        // Replace (not TryAdd) so we override the in-memory default that
        // AddAsteriskSessions() registers.
        services.Replace(ServiceDescriptor.Singleton<SessionStoreBase, PostgresSessionStore>());
        // Forward ISessionStore to the replaced SessionStoreBase so resolving either
        // returns the same singleton instance.
        services.Replace(ServiceDescriptor.Singleton<ISessionStore>(
            sp => sp.GetRequiredService<SessionStoreBase>()));
    }
}
