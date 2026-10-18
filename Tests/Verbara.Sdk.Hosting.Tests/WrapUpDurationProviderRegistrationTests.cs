using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// Which <see cref="CallSessionManager"/> constructor the service container picks under each sessions registration: the
/// one that takes an <see cref="IWrapUpDurationProvider"/> when the host registers one, before or after the sessions
/// registration, and the one without it when the host registers none. No registration code names the constructor; the
/// container takes the longest public one whose parameters it can resolve.
/// </summary>
public sealed class WrapUpDurationProviderRegistrationTests
{
    public enum Registration
    {
        AddVerbaraSessions,
        AddVerbaraSessionsBuilder,
        AddVerbaraSessionsMultiServer,
        AddVerbaraSessionsMultiServerBuilder,
    }

    public enum ProviderPlacement
    {
        None,
        AfterTheSessionsRegistration,
        BeforeTheSessionsRegistration,
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SessionsRegistration_ShouldGiveTheManagerTheRegisteredProviderOrNone_WhenTheProviderIsRegisteredBeforeAfterOrNotAtAll(
        Registration registration, ProviderPlacement placement)
    {
        var provider = new NoAnswerProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        if (placement == ProviderPlacement.BeforeTheSessionsRegistration)
            services.AddSingleton<IWrapUpDurationProvider>(provider);
        Register(services, registration);
        if (placement == ProviderPlacement.AfterTheSessionsRegistration)
            services.AddSingleton<IWrapUpDurationProvider>(provider);

        await using var container = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var manager = container.GetRequiredService<ICallSessionManager>();

        new
        {
            Type = manager.GetType().Name,
            HoldsTheRegisteredProvider = ReferenceEquals((manager as CallSessionManager)?.WrapUpDurations, provider),
            HoldsNone = (manager as CallSessionManager)?.WrapUpDurations is null,
        }.Should().BeEquivalentTo(
            new
            {
                Type = nameof(CallSessionManager),
                HoldsTheRegisteredProvider = placement != ProviderPlacement.None,
                HoldsNone = placement == ProviderPlacement.None,
            },
            $"under {registration}, a provider registered {Describe(placement)} reaches the manager the container "
            + "builds, through the public constructor that takes it, with scopes and the whole graph validated");
    }

    [Theory]
    [MemberData(nameof(HostCases))]
    public void HostBuild_ShouldGiveTheManagerTheRegisteredProviderOrNone_WhenTheProviderIsRegisteredAfterOrNotAtAll(
        Registration registration, ProviderPlacement placement)
    {
        var provider = new NoAnswerProvider();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            DisableDefaults = false,
        });
        Register(builder.Services, registration);
        if (placement == ProviderPlacement.AfterTheSessionsRegistration)
            builder.Services.AddSingleton<IWrapUpDurationProvider>(provider);

        using var host = builder.Build();
        var manager = host.Services.GetRequiredService<ICallSessionManager>() as CallSessionManager;

        new { HoldsTheRegisteredProvider = ReferenceEquals(manager?.WrapUpDurations, provider), HoldsNone = manager?.WrapUpDurations is null }
            .Should().BeEquivalentTo(
                new
                {
                    HoldsTheRegisteredProvider = placement != ProviderPlacement.None,
                    HoldsNone = placement == ProviderPlacement.None,
                },
                $"a host built with {registration} (Development, which validates scopes and the graph on build) "
                + $"gives the manager the provider registered {Describe(placement)}");
    }

    public static TheoryData<Registration, ProviderPlacement> Cases()
    {
        var data = new TheoryData<Registration, ProviderPlacement>();
        foreach (var registration in Enum.GetValues<Registration>())
        {
            foreach (var placement in Enum.GetValues<ProviderPlacement>())
                data.Add(registration, placement);
        }

        return data;
    }

    public static TheoryData<Registration, ProviderPlacement> HostCases()
    {
        var data = new TheoryData<Registration, ProviderPlacement>();
        foreach (var registration in Enum.GetValues<Registration>())
        {
            data.Add(registration, ProviderPlacement.None);
            data.Add(registration, ProviderPlacement.AfterTheSessionsRegistration);
        }

        return data;
    }

    /// <summary>
    /// The SDK's own server registration, then the sessions registration under test: the single-server sessions
    /// registrations attach the single <c>VerbaraServer</c>, the multi-server ones read the pool.
    /// </summary>
    private static void Register(IServiceCollection services, Registration registration)
    {
        switch (registration)
        {
            case Registration.AddVerbaraSessions:
                services.AddVerbara(_ => { });
                services.AddVerbaraSessions();
                break;
            case Registration.AddVerbaraSessionsBuilder:
                services.AddVerbara(_ => { });
                services.AddVerbaraSessionsBuilder();
                break;
            case Registration.AddVerbaraSessionsMultiServer:
                services.AddVerbaraMultiServer();
                services.AddVerbaraSessionsMultiServer();
                break;
            case Registration.AddVerbaraSessionsMultiServerBuilder:
                services.AddVerbaraMultiServer();
                services.AddVerbaraSessionsMultiServerBuilder();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(registration), registration, null);
        }
    }

    private static string Describe(ProviderPlacement placement) => placement switch
    {
        ProviderPlacement.None => "nowhere (none)",
        ProviderPlacement.AfterTheSessionsRegistration => "after the sessions registration",
        _ => "before the sessions registration",
    };

    private sealed class NoAnswerProvider : IWrapUpDurationProvider
    {
        public TimeSpan? GetWrapUpDuration(string serverId, string queueName) => null;
    }
}
