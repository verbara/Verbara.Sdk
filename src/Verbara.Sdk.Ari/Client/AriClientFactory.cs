using Verbara.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Client;

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

    /// <summary>
    /// Called with every client this factory creates, before it is connected or handed out. A test seam
    /// (set through InternalsVisibleTo): it is how a test reaches a client the factory never returns.
    /// </summary>
    internal Action<AriClient>? ClientCreated { get; set; }

    public IAriClient Create(AriClientOptions options)
    {
        var wrappedOptions = Options.Create(options);
        var logger = _loggerFactory.CreateLogger<AriClient>();
        var client = new AriClient(wrappedOptions, logger);
        ClientCreated?.Invoke(client);
        return client;
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
