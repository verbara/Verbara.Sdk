using Asterisk.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ari.Client;

/// <summary>
/// Default factory that creates <see cref="AriClient"/> instances
/// for connecting to multiple Asterisk ARI endpoints.
/// </summary>
public sealed class AriClientFactory : IAriClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public AriClientFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IAriClient Create(AriClientOptions options)
    {
        var wrappedOptions = Options.Create(options);
        var logger = _loggerFactory.CreateLogger<AriClient>();
        return new AriClient(wrappedOptions, logger);
    }

    public async ValueTask<IAriClient> CreateAndConnectAsync(
        AriClientOptions options,
        CancellationToken cancellationToken = default)
    {
        var client = Create(options);
        await client.ConnectAsync(cancellationToken);
        return client;
    }
}
