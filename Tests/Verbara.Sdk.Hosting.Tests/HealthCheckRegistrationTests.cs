using Verbara.Sdk.Ari.Client;
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

    private static readonly string[] SdkCheckNames = ["ami", "live", "agi", "ari", "sessions", "verbara-pool"];

    /// <summary>
    /// A host that names no tag: <c>AddVerbara</c> with ARI configured, a sessions registration and
    /// <c>AddVerbaraMultiServer()</c>, so all six SDK checks are registered.
    /// </summary>
    private static ServiceCollection AllSixChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(o => o.Ari = new AriClientOptions
        {
            BaseUrl = "http://127.0.0.1:8088",
            Username = "asterisk",
            Password = "secret",
            Application = "tags",
            EventsEnabled = false,
        });
        services.AddVerbaraSessionsMultiServer();
        services.AddVerbaraMultiServer();
        return services;
    }

    [Fact]
    public async Task Registrations_ShouldCarryNoTags_WhenNoTagIsNamed()
    {
        var services = AllSixChecks();

        await using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Select(r => r.Name).Should().BeEquivalentTo(SdkCheckNames, "all six SDK checks are registered");
        registrations.Should().OnlyContain(r => r.Tags.Count == 0,
            "the SDK names no tag of its own, so a host that names none gets exactly the untagged registrations of before");
    }

    [Fact]
    public async Task ReadinessPredicate_ShouldSelectNoSdkCheck_WhenNoTagIsNamed()
    {
        var services = AllSixChecks();
        Func<HealthCheckRegistration, bool> readiness = r => r.Tags.Contains("ready");

        await using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Should().HaveCount(SdkCheckNames.Length, "the six SDK checks are there to be filtered");
        registrations.Where(readiness).Should().BeEmpty(
            "an endpoint filtered on the readiness tag reads none of the SDK's checks unless the host tags them");
    }

    [Fact]
    public void HealthCheckRegistration_ShouldCompareTagsIgnoringCase_WhenTheFrameworkBuildsIt()
    {
        var registration = new HealthCheckRegistration("probe", _ => new StubCheck(), failureStatus: null, tags: ["voice"]);

        registration.Tags.Add("Voice").Should().BeFalse("the framework's tag set compares ignoring case, so Voice is voice");
        registration.Tags.Add("ops").Should().BeTrue("the tag set is mutable after the registration is built");
        registration.Tags.Should().HaveCount(2);
    }

    [Fact]
    public void HealthCheckRegistration_ShouldCopyTheTagsItIsGiven_WhenConstructed()
    {
        var source = new List<string> { "voice", "Voice" };

        var registration = new HealthCheckRegistration("probe", _ => new StubCheck(), failureStatus: null, tags: source);
        source.Add("late");

        registration.Tags.Should().ContainSingle("voice and Voice are one tag in the registration's own set")
            .Which.Should().BeEquivalentTo("voice");
        registration.Tags.Should().NotContain("late", "the registration copies the list it is given and does not share it");
    }

    [Fact]
    public async Task AddVerbara_ShouldLeaveAHostsOwnCheckUntagged_WhenTheHostNamesTags()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddCheck<StubCheck>("postgres");
        services.AddVerbara(_ => { });
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("pbx"));

        await using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Single(r => r.Name == "postgres").Tags.Should().BeEmpty(
            "the host-wide tags reach only the checks the SDK registers, never a host's own");
        registrations.Single(r => r.Name == "ami").Tags.Should().BeEquivalentTo(["pbx"],
            "the same provider puts the host's tag on the SDK's ami check");
    }

    private static AriClientOptions TagsAri() => new()
    {
        BaseUrl = "http://127.0.0.1:8088",
        Username = "asterisk",
        Password = "secret",
        Application = "tags",
        EventsEnabled = false,
    };

    private static ICollection<HealthCheckRegistration> Registrations(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

    [Fact]
    public async Task AddVerbara_ShouldTagEveryCheckItRegisters_WhenTheHostNamesTags()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("voice"));
        services.AddVerbara(o => o.Ari = TagsAri());

        await using var provider = services.BuildServiceProvider();
        var registrations = Registrations(provider);

        registrations.Select(r => r.Name).Should().BeEquivalentTo(["ami", "live", "agi", "ari"]);
        registrations.Should().OnlyContain(r => r.Tags.Count == 1 && r.Tags.Contains("voice"),
            "every check AddVerbara registers carries the host-wide tag, and nothing else");
    }

    [Fact]
    public async Task AddVerbara_ShouldTagNoAriCheck_WhenAriIsNotConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("voice"));
        services.AddVerbara(_ => { });

        await using var provider = services.BuildServiceProvider();
        var registrations = Registrations(provider);

        registrations.Select(r => r.Name).Should().BeEquivalentTo(["ami", "live", "agi"],
            "with no ARI section there is no ari check to tag");
        registrations.Should().OnlyContain(r => r.Tags.Contains("voice"));
    }

    [Fact]
    public async Task AddVerbara_ShouldTagTheChecks_WhenTheOptionsAreConfiguredAfterTheCall()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(o => o.Ari = TagsAri());
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("voice"));

        await using var provider = services.BuildServiceProvider();

        Registrations(provider).Should().HaveCount(4).And.OnlyContain(r => r.Tags.Contains("voice"),
            "the tags are read when the health options are first built, after every Configure, whatever the order");
    }

    [Fact]
    public async Task AddVerbara_ShouldAcceptReady_WhenTheHostNamesIt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(o =>
        {
            o.Ami.Username = "admin";
            o.Ami.Password = "secret";
        });
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("ready"));

        await using var provider = services.BuildServiceProvider();
        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow("the SDK does not know the host's vocabulary, so it refuses no tag by name");
        Registrations(provider).Should().OnlyContain(r => r.Tags.Contains("ready"),
            "the host named it, so the host's own choice is applied (the docs warn against it)");
    }

    private sealed class StubCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
