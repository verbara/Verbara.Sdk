using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Agi.Mapping;
using Asterisk.NetAot.Ami.Transport;
using Asterisk.NetAot.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.NetAot.IntegrationTests.Di;

public class ServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(bool includeAri = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskNetAot(options =>
        {
            options.Ami.Hostname = "localhost";
            options.Ami.Username = "admin";
            options.Ami.Password = "secret";

            if (includeAri)
            {
                options.Ari = new Asterisk.NetAot.Ari.Client.AriClientOptions
                {
                    BaseUrl = "http://localhost:8088",
                    Username = "ari",
                    Password = "ari",
                    Application = "test"
                };
            }
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ShouldResolve_IAmiConnection()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetService<IAmiConnection>();
        connection.Should().NotBeNull();
    }

    [Fact]
    public async Task ShouldResolve_ISocketConnectionFactory()
    {
        await using var provider = BuildProvider();
        var factory = provider.GetService<ISocketConnectionFactory>();
        factory.Should().NotBeNull()
            .And.BeOfType<PipelineSocketConnectionFactory>();
    }

    [Fact]
    public async Task ShouldResolve_IMappingStrategy()
    {
        await using var provider = BuildProvider();
        var strategy = provider.GetService<IMappingStrategy>();
        strategy.Should().NotBeNull()
            .And.BeOfType<SimpleMappingStrategy>();
    }

    [Fact]
    public async Task ShouldResolve_IAgiServer()
    {
        await using var provider = BuildProvider();
        var server = provider.GetService<IAgiServer>();
        server.Should().NotBeNull();
    }

    [Fact]
    public async Task ShouldResolve_AsteriskServer()
    {
        await using var provider = BuildProvider();
        var server = provider.GetService<AsteriskServer>();
        server.Should().NotBeNull();
    }

    [Fact]
    public async Task ShouldResolve_IAriClient_WhenConfigured()
    {
        await using var provider = BuildProvider(includeAri: true);
        var client = provider.GetService<IAriClient>();
        client.Should().NotBeNull();
    }

    [Fact]
    public async Task ShouldNotResolve_IAriClient_WhenNotConfigured()
    {
        await using var provider = BuildProvider(includeAri: false);
        var client = provider.GetService<IAriClient>();
        client.Should().BeNull();
    }

    [Fact]
    public async Task ShouldResolve_Singleton_AmiConnection()
    {
        await using var provider = BuildProvider();
        var connection1 = provider.GetService<IAmiConnection>();
        var connection2 = provider.GetService<IAmiConnection>();
        connection1.Should().BeSameAs(connection2);
    }
}
