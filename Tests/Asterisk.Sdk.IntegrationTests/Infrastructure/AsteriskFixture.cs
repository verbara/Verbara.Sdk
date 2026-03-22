using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.TestInfrastructure.Stacks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.IntegrationTests.Infrastructure;

/// <summary>
/// Helper that provides credential defaults and factory methods for integration tests.
/// Host and port are read from the Testcontainers IntegrationFixture (or env vars as fallback).
/// </summary>
public static class AsteriskFixture
{
    public static string AmiUsername =>
        Environment.GetEnvironmentVariable("ASTERISK_AMI_USERNAME") ?? "testadmin";
    public static string AmiPassword =>
        Environment.GetEnvironmentVariable("ASTERISK_AMI_PASSWORD") ?? "testpass";
    public static string AriUsername =>
        Environment.GetEnvironmentVariable("ASTERISK_ARI_USERNAME") ?? "testari";
    public static string AriPassword =>
        Environment.GetEnvironmentVariable("ASTERISK_ARI_PASSWORD") ?? "testari";
    public static string AriApp =>
        Environment.GetEnvironmentVariable("ASTERISK_ARI_APP") ?? "test-app";

    public static AmiConnection CreateAmiConnection(IntegrationFixture fixture)
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = fixture.Asterisk.Host,
            Port = fixture.Asterisk.AmiPort,
            Username = AmiUsername,
            Password = AmiPassword
        });

        return new AmiConnection(
            options,
            new PipelineSocketConnectionFactory(),
            NullLogger<AmiConnection>.Instance);
    }

    public static AriClient CreateAriClient(IntegrationFixture fixture)
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://{fixture.Asterisk.Host}:{fixture.Asterisk.AriPort}",
            Username = AriUsername,
            Password = AriPassword,
            Application = AriApp
        });

        return new AriClient(options, NullLogger<AriClient>.Instance);
    }
}
