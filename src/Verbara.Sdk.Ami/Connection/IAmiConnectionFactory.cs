using Verbara.Sdk;

namespace Verbara.Sdk.Ami.Connection;

/// <summary>
/// Factory for creating AMI connections to multiple Asterisk servers.
/// Enables multi-server deployments where 100K+ agents are distributed
/// across 20-50 Asterisk instances.
/// </summary>
public interface IAmiConnectionFactory
{
    /// <summary>Create a new AMI connection with the specified options (not yet connected).</summary>
    IAmiConnection Create(AmiConnectionOptions options);

    /// <summary>
    /// Create a new AMI connection and connect immediately. When the connect fails, the connection is disposed
    /// before the error is rethrown, so the caller has nothing to release.
    /// </summary>
    ValueTask<IAmiConnection> CreateAndConnectAsync(
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default);
}
