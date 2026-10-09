using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// Which health checks each registration method installs. <c>AddVerbara</c> registers <c>ami</c>, <c>live</c> and
/// <c>agi</c>; <c>AddVerbaraMultiServer</c> registers one check that reads every server of the pool, once, however many
/// times it is called, under the name <c>verbara-pool</c> and with no tags (the owner's ruling, design D9, Q2,
/// 2026-09-30).
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
        var pool = registrations.Should().ContainSingle("a multi-server host gets one health check that reads its servers' connections")
            .Which;
        pool.Name.Should().Be("verbara-pool");
        pool.Tags.Should().BeEmpty("an untagged check stays out of endpoints filtered on a tag, such as a readiness probe");
        pool.Factory(provider).Should().BeOfType<VerbaraServerPoolHealthCheck>();
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
        var report = (await run.Should().NotThrowAsync(
            "a second call registers nothing, so no duplicate name reaches the health check service")).Subject;
        report.Entries.Should().ContainSingle().Which.Key.Should().Be("verbara-pool");
        report.Entries["verbara-pool"].Status.Should().Be(HealthStatus.Healthy, "an empty pool holds no servers yet");
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Should().ContainSingle("the pool check is registered once, however many times the method is called")
            .Which.Name.Should().Be("verbara-pool");
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

    public enum SessionsTwice
    {
        MultiServerTwice,
        SingleAndMultiServer,
    }

    [Theory]
    [InlineData(SessionsTwice.MultiServerTwice)]
    [InlineData(SessionsTwice.SingleAndMultiServer)]
    public async Task SessionsRegistrations_ShouldRegisterOneSessionsCheck_WhenTheSessionEngineIsRegisteredTwice(
        SessionsTwice registrations)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (registrations == SessionsTwice.SingleAndMultiServer)
            services.AddVerbaraSessions();
        else
            services.AddVerbaraSessionsMultiServer();
        services.AddVerbaraSessionsMultiServer();

        await using var provider = services.BuildServiceProvider();
        var run = async () => await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var report = (await run.Should().NotThrowAsync(
            "a second sessions registration adds no second check under the same name")).Subject;
        report.Entries.Keys.Should().Equal(["sessions"], "the sessions check is registered once");
    }
}
