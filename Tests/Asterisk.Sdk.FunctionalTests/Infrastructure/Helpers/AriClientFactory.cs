namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using Asterisk.Sdk.Ari.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public static class AriClientFactory
{
    public static string Host =>
        Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
    public static int HttpPort =>
        int.Parse(Environment.GetEnvironmentVariable("ASTERISK_ARI_PORT") ?? "8088",
            System.Globalization.CultureInfo.InvariantCulture);
    public static string Username =>
        Environment.GetEnvironmentVariable("ASTERISK_ARI_USERNAME") ?? "testari";
    public static string Password =>
        Environment.GetEnvironmentVariable("ASTERISK_ARI_PASSWORD") ?? "testari";

    public static AriClient Create(
        ILoggerFactory? loggerFactory = null,
        string application = "test-app",
        Action<AriClientOptions>? configure = null)
    {
        var options = new AriClientOptions
        {
            BaseUrl = $"http://{Host}:{HttpPort}",
            Username = Username,
            Password = Password,
            Application = application,
            AutoReconnect = false
        };
        configure?.Invoke(options);

        var wrappedOptions = Options.Create(options);
        var logger = loggerFactory?.CreateLogger<AriClient>() ?? NullLogger<AriClient>.Instance;

        return new AriClient(wrappedOptions, logger);
    }
}
