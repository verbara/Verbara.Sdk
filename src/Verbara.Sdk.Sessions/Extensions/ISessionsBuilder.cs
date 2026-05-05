using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Sdk.Sessions.Extensions;

/// <summary>
/// Fluent builder returned by <c>AddVerbaraSessionsBuilder</c> / <c>AddVerbaraSessionsMultiServerBuilder</c>
/// in <c>Verbara.Sdk.Hosting</c>. Acts as the entry point for backend-specific
/// registration extensions such as <c>UseInMemory()</c>, <c>UseRedis(...)</c>,
/// and <c>UsePostgres(...)</c>.
/// </summary>
/// <remarks>
/// Example usage:
/// <code>
/// services.AddVerbaraSessionsBuilder()
///         .UseRedis(options => options.ConnectionString = "localhost:6379");
/// </code>
/// </remarks>
public interface ISessionsBuilder
{
    /// <summary>
    /// The underlying <see cref="IServiceCollection"/> that backend extensions should
    /// register their services into.
    /// </summary>
    IServiceCollection Services { get; }
}
