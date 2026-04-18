using Asterisk.Sdk.Sessions.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.Sessions.Extensions;

/// <summary>
/// Default <see cref="ISessionsBuilder"/> implementation. Internal so that the
/// interface remains the public contract and backend packages depend only on
/// <see cref="ISessionsBuilder"/>.
/// </summary>
internal sealed class SessionsBuilder : ISessionsBuilder
{
    public SessionsBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }
}

/// <summary>
/// Extension methods on <see cref="ISessionsBuilder"/> for registering built-in
/// session store backends. Additional backends (Redis, Postgres, ...) ship as
/// separate NuGet packages that add their own <c>Use*</c> extensions here.
/// </summary>
public static class AsteriskSessionsBuilderExtensions
{
    /// <summary>
    /// Register the default in-memory session store. This is already registered
    /// by <c>AddAsteriskSessions()</c> / <c>AddAsteriskSessionsMultiServer()</c>,
    /// but calling this explicitly documents the choice and is idempotent thanks
    /// to <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>.
    /// </summary>
    /// <param name="builder">The sessions builder.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static ISessionsBuilder UseInMemory(this ISessionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<SessionStoreBase, InMemorySessionStore>();
        builder.Services.TryAddSingleton<ISessionStore>(sp => sp.GetRequiredService<SessionStoreBase>());
        return builder;
    }
}
