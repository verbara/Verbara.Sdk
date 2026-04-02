using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Client;

/// <summary>
/// Factory for creating ARI client instances for multi-server deployments.
/// Enables per-node ARI connections where each Asterisk server has its own
/// ARI endpoint with independent credentials.
/// </summary>
public interface IAriClientFactory
{
    /// <summary>Create a new ARI client with the specified options (not yet connected).</summary>
    IAriClient Create(AriClientOptions options);

    /// <summary>Create a new ARI client and connect immediately.</summary>
    ValueTask<IAriClient> CreateAndConnectAsync(
        AriClientOptions options,
        CancellationToken cancellationToken = default);
}
