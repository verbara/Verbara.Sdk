using Verbara.Sdk.Hosting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// Which health checks each registration method installs. <c>AddVerbara</c> registers <c>ami</c>, <c>live</c> and
/// <c>agi</c>; <c>AddVerbaraMultiServer</c> registers one check that reads every server of the pool, once, however many
/// times it is called. The pool check's name and tags are the owner's ruling (design D9, Q2); these tests count
/// registrations and do not presume them.
/// </summary>
public sealed class HealthCheckRegistrationTests
{
    [Fact]
    public async Task AddVerbaraMultiServer_ShouldRegisterAPoolHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddVerbaraMultiServer();

        await using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().NotBeEmpty("a multi-server host gets a health check that reads its servers' connections");
    }

    [Fact]
    public async Task AddVerbaraMultiServer_ShouldRegisterOnePoolCheck_WhenCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddVerbaraMultiServer();
        services.AddVerbaraMultiServer();

        await using var provider = services.BuildServiceProvider();
        var health = provider.GetService<HealthCheckService>();
        health.Should().NotBeNull("the registration installs the health check service");
        var run = async () => await health!.CheckHealthAsync();
        await run.Should().NotThrowAsync("a second call registers nothing, so no duplicate name reaches the health check service");
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Should().ContainSingle("the pool check is registered once, however many times the method is called");
    }

    [Fact]
    public async Task AddVerbara_ShouldRegisterTheSameHealthChecks_WhenCalled()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddVerbara(_ => { });

        await using var provider = services.BuildServiceProvider();
        var names = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Select(r => r.Name);
        names.Should().BeEquivalentTo(["ami", "live", "agi"], "a single-server host's checks do not change");
    }
}
