using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ami.Connection;

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
        await connection.ConnectAsync(cancellationToken);
        return connection;
    }
}
