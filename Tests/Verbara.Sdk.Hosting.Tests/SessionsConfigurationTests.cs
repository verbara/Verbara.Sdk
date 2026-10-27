using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>Options delivered through the four public configuration registrations, without an Asterisk connection.</summary>
public sealed class SessionsConfigurationTests
{
    public enum Registration { Server, ServerBuilder, Pool, PoolBuilder }

    public static TheoryData<Registration> Registrations => new()
    {
        Registration.Server, Registration.ServerBuilder, Registration.Pool, Registration.PoolBuilder,
    };

    public static TheoryData<Registration, string> ListKeys => new()
    {
        { Registration.Server, "InboundContextPatterns" }, { Registration.Server, "OutboundContextPatterns" },
        { Registration.ServerBuilder, "InboundContextPatterns" }, { Registration.ServerBuilder, "OutboundContextPatterns" },
        { Registration.Pool, "InboundContextPatterns" }, { Registration.Pool, "OutboundContextPatterns" },
        { Registration.PoolBuilder, "InboundContextPatterns" }, { Registration.PoolBuilder, "OutboundContextPatterns" },
    };

    public static TheoryData<Registration, string> MalformedKeys => new()
    {
        { Registration.Server, "DialingTimeout" }, { Registration.Server, "MaxCompletedSessions" },
        { Registration.ServerBuilder, "DialingTimeout" }, { Registration.ServerBuilder, "MaxCompletedSessions" },
        { Registration.Pool, "DialingTimeout" }, { Registration.Pool, "MaxCompletedSessions" },
        { Registration.PoolBuilder, "DialingTimeout" }, { Registration.PoolBuilder, "MaxCompletedSessions" },
    };

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Options_ShouldReceiveEverySettableValue_WhenTheSessionsSectionIsConfigured(Registration registration)
    {
        using var configuration = Configuration(new()
        {
            ["Sessions:ReconciliationInterval"] = "00:00:10",
            ["Sessions:DialingTimeout"] = "00:00:21",
            ["Sessions:RingingTimeout"] = "00:00:22",
            ["Sessions:MaxCompletedSessions"] = "73",
            ["Sessions:CompletedRetention"] = "00:05:00",
            ["Sessions:QueueMetricsWindow"] = "00:06:00",
            ["Sessions:SlaThreshold"] = "00:00:07",
            ["Sessions:WrapUpDuration"] = "00:00:08",
            ["Sessions:InboundContextPatterns:0"] = "from-carrier",
            ["Sessions:OutboundContextPatterns:0"] = "to-carrier",
        });
        using var provider = Provider(registration, configuration);

        var options = provider.GetRequiredService<IOptions<SessionOptions>>().Value;

        options.Should().BeEquivalentTo(new SessionOptions
        {
            ReconciliationInterval = TimeSpan.FromSeconds(10), DialingTimeout = TimeSpan.FromSeconds(21),
            RingingTimeout = TimeSpan.FromSeconds(22), MaxCompletedSessions = 73,
            CompletedRetention = TimeSpan.FromMinutes(5), QueueMetricsWindow = TimeSpan.FromMinutes(6),
            SlaThreshold = TimeSpan.FromSeconds(7), WrapUpDuration = TimeSpan.FromSeconds(8),
            InboundContextPatterns = ["from-carrier"], OutboundContextPatterns = ["to-carrier"],
        });
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Options_ShouldKeepEveryDefault_WhenTheSessionsSectionIsAbsent(Registration registration)
    {
        using var configuration = Configuration(new() { ["DialingTimeout"] = "soon", ["Other:CompletedRetention"] = "soon" });
        using var provider = Provider(registration, configuration);

        provider.GetRequiredService<IOptions<SessionOptions>>().Value.Should().BeEquivalentTo(new SessionOptions());
    }

    [Theory]
    [MemberData(nameof(ListKeys))]
    public void Options_ShouldReplaceOnlyTheConfiguredList_WhenTheOtherListIsAbsent(Registration registration, string key)
    {
        using var configuration = Configuration(new() { [$"Sessions:{key}:0"] = "carrier" });
        using var provider = Provider(registration, configuration);
        var options = provider.GetRequiredService<IOptions<SessionOptions>>().Value;

        if (key == "InboundContextPatterns")
        {
            options.InboundContextPatterns.Should().Equal("carrier");
            options.OutboundContextPatterns.Should().Equal("from-internal", "from-sip", "from-users");
        }
        else
        {
            options.OutboundContextPatterns.Should().Equal("carrier");
            options.InboundContextPatterns.Should().Equal("from-trunk", "from-pstn", "from-external");
        }
    }

    [Theory]
    [MemberData(nameof(ListKeys))]
    public void Options_ShouldClearTheDefaultList_WhenTheProviderExposesAnEmptyListKey(Registration registration, string key)
    {
        using var configuration = Configuration(new() { [$"Sessions:{key}"] = null });
        using var provider = Provider(registration, configuration);
        var options = provider.GetRequiredService<IOptions<SessionOptions>>().Value;

        (key == "InboundContextPatterns" ? options.InboundContextPatterns : options.OutboundContextPatterns)
            .Should().BeEmpty("the provider exposes the list key even though its section has no value or children");
        (key == "InboundContextPatterns" ? options.OutboundContextPatterns : options.InboundContextPatterns)
            .Should().HaveCount(3, "the absent list retains its defaults independently");
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Options_ShouldLetTheDelegateReadAndAdjustBoundValues_WhenADelegateIsSupplied(Registration registration)
    {
        using var configuration = Configuration(new()
        {
            ["Sessions:CompletedRetention"] = "00:05:00", ["Sessions:InboundContextPatterns:0"] = "carrier",
        });
        TimeSpan? seenRetention = null;
        string[]? seenPatterns = null;
        using var provider = Provider(registration, configuration, options =>
        {
            seenRetention = options.CompletedRetention;
            seenPatterns = options.InboundContextPatterns;
            options.CompletedRetention = TimeSpan.FromMinutes(1);
            options.InboundContextPatterns = [.. options.InboundContextPatterns, "extra"];
        });

        var resolved = provider.GetRequiredService<IOptions<SessionOptions>>().Value;

        seenRetention.Should().Be(TimeSpan.FromMinutes(5));
        seenPatterns.Should().Equal("carrier");
        resolved.CompletedRetention.Should().Be(TimeSpan.FromMinutes(1));
        resolved.InboundContextPatterns.Should().Equal("carrier", "extra");
    }

    [Theory]
    [MemberData(nameof(MalformedKeys))]
    public void Options_ShouldFailNamingTheKey_WhenTheConfiguredValueCannotBeConverted(Registration registration, string key)
    {
        using var configuration = Configuration(new() { [$"Sessions:{key}"] = "soon" });
        using var provider = Provider(registration, configuration);

        var resolve = () => provider.GetRequiredService<IOptions<SessionOptions>>().Value;

        resolve.Should().Throw<InvalidOperationException>().WithMessage($"*Sessions:{key}*");
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Options_ShouldUseInvariantCulture_WhenTheCurrentCultureUsesCommaFractions(Registration registration)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var configuration = Configuration(new() { ["Sessions:DialingTimeout"] = "1.02:03:04.005" });
            using var provider = Provider(registration, configuration);

            provider.GetRequiredService<IOptions<SessionOptions>>().Value.DialingTimeout.Should()
                .Be(new TimeSpan(1, 2, 3, 4, 5));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task Host_ShouldRejectTheSameValueAsInline_WhenOptionsValidationFails(Registration registration)
    {
        using var configuration = Configuration(new() { ["Sessions:MaxCompletedSessions"] = "0" });
        using var configured = Host(registration, configuration);
        using var inline = Host(registration, configure: options => options.MaxCompletedSessions = 0);

        var configuredFailure = await Record.ExceptionAsync(() => configured.StartAsync());
        var inlineFailure = await Record.ExceptionAsync(() => inline.StartAsync());

        configuredFailure.Should().BeOfType<OptionsValidationException>().Which.Failures
            .Should().Equal(((OptionsValidationException)inlineFailure!).Failures);
        configuredFailure.Message.Should().Contain("MaxCompletedSessions");
    }

    [Theory]
    [InlineData(Registration.Pool)]
    [InlineData(Registration.PoolBuilder)]
    public async Task Host_ShouldStartNoSweepTimer_WhenConfigurationSwitchesTheSweepOff(Registration registration)
    {
        using var configuration = Configuration(new() { ["Sessions:ReconciliationInterval"] = "-00:00:00.001" });
        var clock = Substitute.For<TimeProvider>();
        using var host = Host(registration, configuration, clock: clock);

        await host.StartAsync();
        await host.StopAsync();

        host.Services.GetRequiredService<IOptions<SessionOptions>>().Value.ReconciliationInterval.Should().Be(Timeout.InfiniteTimeSpan);
        clock.DidNotReceive().CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>());
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Registration_ShouldMatchTheInlineServices_WhenConfigurationIsUsed(Registration registration)
    {
        using var configuration = Configuration(new());
        var configured = new ServiceCollection();
        var inline = new ServiceCollection();

        Register(configured, registration, configuration);
        Register(inline, registration, configure: _ => { });

        Describe(configured).Should().Equal(Describe(inline), "only the configure callback's contents change");
    }

    [Fact]
    public void Registration_ShouldStillAcceptLiteralNull_WhenTheExistingActionOverloadIsCalled()
    {
        var services = new ServiceCollection();
        services.AddVerbaraSessions(null).Should().BeSameAs(services);
        services.AddVerbaraSessionsBuilder(null).Services.Should().BeSameAs(services);
        services.AddVerbaraSessionsMultiServer(null).Should().BeSameAs(services);
        services.AddVerbaraSessionsMultiServerBuilder(null).Services.Should().BeSameAs(services);
    }

    private static IEnumerable<string> Describe(IServiceCollection services) => services.Select(service =>
        $"{service.ServiceType}:{service.Lifetime}:{service.ImplementationType}:{service.ImplementationFactory is not null}:{service.ImplementationInstance?.GetType()}");

    private static ConfigurationRoot Configuration(Dictionary<string, string?> values) =>
        (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider Provider(Registration registration, IConfiguration configuration, Action<SessionOptions>? configure = null)
    {
        var services = new ServiceCollection();
        Register(services, registration, configuration, configure);
        return services.BuildServiceProvider();
    }

    private static IHost Host(Registration registration, IConfiguration? configuration = null, Action<SessionOptions>? configure = null, TimeProvider? clock = null) =>
        new HostBuilder().ConfigureServices(services =>
        {
            services.AddVerbaraMultiServer();
            services.AddSingleton<ICallSessionManager>(Substitute.For<ICallSessionManager>());
            services.AddSingleton(_ => new VerbaraServer(Substitute.For<IAmiConnection>(), NullLogger<VerbaraServer>.Instance));
            if (clock is not null) services.AddSingleton(clock);
            Register(services, registration, configuration, configure);
        }).Build();

    private static IServiceCollection Register(IServiceCollection services, Registration registration, IConfiguration? configuration = null, Action<SessionOptions>? configure = null) =>
        (registration, configuration) switch
        {
            (Registration.Server, null) => services.AddVerbaraSessions(configure),
            (Registration.ServerBuilder, null) => services.AddVerbaraSessionsBuilder(configure).Services,
            (Registration.Pool, null) => services.AddVerbaraSessionsMultiServer(configure),
            (Registration.PoolBuilder, null) => services.AddVerbaraSessionsMultiServerBuilder(configure).Services,
            (Registration.Server, _) => services.AddVerbaraSessions(configuration, configure),
            (Registration.ServerBuilder, _) => services.AddVerbaraSessionsBuilder(configuration, configure).Services,
            (Registration.Pool, _) => services.AddVerbaraSessionsMultiServer(configuration, configure),
            (Registration.PoolBuilder, _) => services.AddVerbaraSessionsMultiServerBuilder(configuration, configure).Services,
            _ => throw new ArgumentOutOfRangeException(nameof(registration)),
        };
}
