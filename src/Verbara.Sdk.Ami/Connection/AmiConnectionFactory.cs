using Verbara.Sdk;
using Verbara.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Connection;

/// <summary>
/// Default factory that creates <see cref="AmiConnection"/> instances
/// for connecting to multiple Asterisk servers.
/// </summary>
public sealed class AmiConnectionFactory : IAmiConnectionFactory
{
    private readonly ISocketConnectionFactory _socketFactory;
    private readonly ILoggerFactory _loggerFactory;

    public AmiConnectionFactory(
        ISocketConnectionFactory socketFactory,
        ILoggerFactory loggerFactory)
    {
        _socketFactory = socketFactory;
        _loggerFactory = loggerFactory;
    }

    public IAmiConnection Create(AmiConnectionOptions options)
    {
        var wrappedOptions = Options.Create(options);
        var logger = _loggerFactory.CreateLogger<AmiConnection>();
        return new AmiConnection(wrappedOptions, _socketFactory, logger);
    }

    public async ValueTask<IAmiConnection> CreateAndConnectAsync(
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var connection = Create(options);
        try
        {
            await connection.ConnectAsync(cancellationToken);
        }
        catch
        {
            // The caller never receives a connection whose connect failed, so it could never dispose it: release
            // the socket, and whatever else the failed connect left open, here. The error, a cancellation
            // included, then reaches the caller unchanged.
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }
}
