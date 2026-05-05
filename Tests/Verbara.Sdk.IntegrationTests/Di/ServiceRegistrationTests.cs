using Verbara.Sdk;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Sdk.IntegrationTests.Di;

public class ServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(bool includeAri = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(options =>
        {
            options.Ami.Hostname = "localhost";
            options.Ami.Username = "admin";
            options.Ami.Password = "secret";

            if (includeAri)
            {
                options.Ari = new Verbara.Sdk.Ari.Client.AriClientOptions
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
    public async Task ShouldResolve_VerbaraServer()
    {
        await using var provider = BuildProvider();
        var server = provider.GetService<VerbaraServer>();
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

    [Fact]
    public async Task AddVerbara_ShouldRegisterHostedServices()
    {
        await using var provider = BuildProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        hostedServices.Should().Contain(s => s is AmiConnectionHostedService);
        hostedServices.Should().Contain(s => s is VerbaraServerHostedService);
    }

    [Fact]
    public async Task AddVerbaraSessions_ShouldRegisterHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(options =>
        {
            options.Ami.Hostname = "localhost";
            options.Ami.Username = "admin";
            options.Ami.Password = "secret";
        });
        services.AddVerbaraSessions();

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        hostedServices.Should().Contain(s => s.GetType().Name == "SessionManagerHostedService");
    }

    [Fact]
    public async Task AddVerbaraSessionsMultiServer_ShouldNotRegisterHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraMultiServer();
        services.AddVerbaraSessionsMultiServer();

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        hostedServices.Should().NotContain(s => s.GetType().Name == "SessionManagerHostedService");
    }

    [Fact]
    public async Task AddVerbaraSessionsMultiServer_ShouldRegisterCoreServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraMultiServer();
        services.AddVerbaraSessionsMultiServer();

        await using var provider = services.BuildServiceProvider();

        provider.GetService<ICallSessionManager>().Should().NotBeNull();
        provider.GetService<SessionStoreBase>().Should().NotBeNull();
    }
}
