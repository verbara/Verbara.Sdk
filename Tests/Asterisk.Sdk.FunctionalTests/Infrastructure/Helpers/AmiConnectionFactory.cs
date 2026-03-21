namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public static class AmiConnectionFactory
{
    public static string Host =>
        Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
    public static int Port =>
        int.Parse(Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT") ?? "5038",
            System.Globalization.CultureInfo.InvariantCulture);
    public static string Username =>
        Environment.GetEnvironmentVariable("ASTERISK_AMI_USERNAME") ?? "testadmin";
    public static string Password =>
        Environment.GetEnvironmentVariable("ASTERISK_AMI_PASSWORD") ?? "testpass";

    public static AmiConnection Create(
        ILoggerFactory? loggerFactory = null,
        Action<AmiConnectionOptions>? configure = null)
    {
        var options = new AmiConnectionOptions
        {
            Hostname = Host,
            Port = Port,
            Username = Username,
            Password = Password
        };
        configure?.Invoke(options);

        var wrappedOptions = Options.Create(options);
        var socketFactory = new PipelineSocketConnectionFactory();
        var logger = loggerFactory?.CreateLogger<AmiConnection>() ?? NullLogger<AmiConnection>.Instance;

        return new AmiConnection(wrappedOptions, socketFactory, logger);
    }
}
