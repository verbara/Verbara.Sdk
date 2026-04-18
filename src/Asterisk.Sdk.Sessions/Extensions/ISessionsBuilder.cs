using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Sdk.Sessions.Extensions;

/// <summary>
/// Fluent builder returned by <c>AddAsteriskSessionsBuilder</c> / <c>AddAsteriskSessionsMultiServerBuilder</c>
/// in <c>Asterisk.Sdk.Hosting</c>. Acts as the entry point for backend-specific
/// registration extensions such as <c>UseInMemory()</c>, <c>UseRedis(...)</c>,
/// and <c>UsePostgres(...)</c>.
/// </summary>
/// <remarks>
/// Example usage:
/// <code>
/// services.AddAsteriskSessionsBuilder()
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
