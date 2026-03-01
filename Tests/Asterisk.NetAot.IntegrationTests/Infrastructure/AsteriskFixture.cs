using Asterisk.NetAot.Ami.Connection;
using Asterisk.NetAot.Ami.Transport;
using Asterisk.NetAot.Ari.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.NetAot.IntegrationTests.Infrastructure;

/// <summary>
/// Shared fixture for integration tests that require a running Asterisk instance.
/// Reads connection settings from environment variables (set by docker-compose).
/// </summary>
public sealed class AsteriskFixture : IAsyncLifetime
{
    public string Host { get; } = Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
    public int AmiPort { get; } = int.Parse(Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT") ?? "5038", System.Globalization.CultureInfo.InvariantCulture);
    public string AmiUsername { get; } = Environment.GetEnvironmentVariable("ASTERISK_AMI_USERNAME") ?? "testadmin";
    public string AmiPassword { get; } = Environment.GetEnvironmentVariable("ASTERISK_AMI_PASSWORD") ?? "testpass";
    public int AgiPort { get; } = int.Parse(Environment.GetEnvironmentVariable("ASTERISK_AGI_PORT") ?? "4573", System.Globalization.CultureInfo.InvariantCulture);
    public int AriPort { get; } = int.Parse(Environment.GetEnvironmentVariable("ASTERISK_ARI_PORT") ?? "8088", System.Globalization.CultureInfo.InvariantCulture);
    public string AriUsername { get; } = Environment.GetEnvironmentVariable("ASTERISK_ARI_USERNAME") ?? "testari";
    public string AriPassword { get; } = Environment.GetEnvironmentVariable("ASTERISK_ARI_PASSWORD") ?? "testari";
    public string AriApp { get; } = Environment.GetEnvironmentVariable("ASTERISK_ARI_APP") ?? "test-app";

    public AmiConnection CreateAmiConnection()
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = Host,
            Port = AmiPort,
            Username = AmiUsername,
            Password = AmiPassword
        });

        return new AmiConnection(
            options,
            new PipelineSocketConnectionFactory(),
            NullLogger<AmiConnection>.Instance);
    }

    public AriClient CreateAriClient()
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://{Host}:{AriPort}",
            Username = AriUsername,
            Password = AriPassword,
            Application = AriApp
        });

        return new AriClient(options, NullLogger<AriClient>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}
