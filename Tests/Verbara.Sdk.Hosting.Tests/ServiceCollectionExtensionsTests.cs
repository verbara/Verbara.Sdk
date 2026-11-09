using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ari.Client;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// What <c>AddVerbara</c> hands the clients it registers. Nothing here starts a host: <c>AddVerbara</c> registers hosted
/// services that dial AMI on localhost:5038 and bind the AGI port, so a started host would fail on whatever the machine's
/// network does. Start-time validation is driven through <see cref="IStartupValidator"/>, which is what a host's start
/// runs for <c>ValidateOnStart</c>, and a client through its DI registration.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task StartupValidation_ShouldFail_WhenConfiguredWithAnUnusableMultiplier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(o =>
        {
            o.Ami.Username = "admin";
            o.Ami.Password = "secret";
        });
        services.Configure<AmiConnectionOptions>(o => o.ReconnectMultiplier = 0.5);
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IStartupValidator>();

        var act = () => validator.Validate();

        act.Should().Throw<OptionsValidationException>(
                "a host that validates on start must not start with AutoReconnect on and a multiplier the backoff cannot use")
            .Which.Message.Should().Contain(nameof(AmiConnectionOptions.ReconnectMultiplier), "the failure names the option to fix");
    }

    [Fact]
    public async Task IAriClient_ShouldThrowNamingTheOption_WhenResolvedWithAnUnusableMultiplier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(o =>
        {
            o.Ami.Username = "admin";
            o.Ami.Password = "secret";
            o.Ari = new AriClientOptions
            {
                BaseUrl = "http://localhost:8088",
                Username = "asterisk",
                Password = "asterisk",
                Application = "test-app",
            };
        });
        services.Configure<AriClientOptions>(o =>
        {
            o.AutoReconnect = true;
            o.ReconnectMultiplier = 0.5;
        });
        // Without the validator, the client's constructor is what must reject the value.
        services.RemoveAll<IValidateOptions<AriClientOptions>>();
        await using var provider = services.BuildServiceProvider();
        IAriClient? resolved = null;

        var act = () => resolved = provider.GetRequiredService<IAriClient>();

        act.Should().Throw<ArgumentOutOfRangeException>(
                "the registered client is built by its constructor, which rejects a multiplier below 1 with AutoReconnect on")
            .Which.ParamName.Should().Be(nameof(AriClientOptions.ReconnectMultiplier), "the error names the option to fix");
        resolved.Should().BeNull();
    }

    private static readonly string[] VoiceAndPbx = ["voice", "pbx"];
    private static readonly string[] OpsAndVoice = ["ops", "voice"];

    private static readonly Dictionary<string, string?> AriSection = new()
    {
        ["Asterisk:Ari:BaseUrl"] = "http://127.0.0.1:8088",
        ["Asterisk:Ari:Username"] = "asterisk",
        ["Asterisk:Ari:Password"] = "secret",
        ["Asterisk:Ari:Application"] = "tags",
        ["Asterisk:Ari:EventsEnabled"] = "false",
    };

    private static IConfiguration WithAri(params (string Key, string? Value)[] extra)
    {
        var values = new Dictionary<string, string?>(AriSection);
        foreach (var (key, value) in extra)
            values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ICollection<HealthCheckRegistration> Registrations(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

    [Fact]
    public async Task AddVerbaraConfiguration_ShouldTagTheChecks_WhenAsteriskHealthCheckTagsIsSet()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(WithAri(("Asterisk:HealthCheckTags:0", "voice"), ("Asterisk:HealthCheckTags:1", "pbx")));

        await using var provider = services.BuildServiceProvider();
        var registrations = Registrations(provider);

        registrations.Select(r => r.Name).Should().BeEquivalentTo(["ami", "live", "agi", "ari"]);
        registrations.Should().OnlyContain(r => r.Tags.SetEquals(VoiceAndPbx),
            "the configuration key names the tags every SDK check carries");
    }

    [Fact]
    public async Task AddVerbaraConfiguration_ShouldMergeTheKeyWithAConfigure_WhenBothAreSet()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("ops"));
        services.AddVerbara(WithAri(("Asterisk:HealthCheckTags:0", "voice")));
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("Voice"));

        await using var provider = services.BuildServiceProvider();

        Registrations(provider).Should().HaveCount(4).And.OnlyContain(r => r.Tags.SetEquals(OpsAndVoice),
            "the key and every Configure add to one set, and voice and Voice are one tag");
    }

    [Fact]
    public async Task AddVerbaraConfiguration_ShouldTagNothing_WhenTheKeyIsAbsent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(WithAri());

        await using var provider = services.BuildServiceProvider();

        Registrations(provider).Should().HaveCount(4).And.OnlyContain(r => r.Tags.Count == 0,
            "with no key and no Configure the SDK's checks are untagged, as before");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AddVerbaraConfiguration_ShouldFailNamingTags_WhenAKeyValueIsBlank(string blank)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(WithAri(("Asterisk:HealthCheckTags:0", blank)));

        await using var provider = services.BuildServiceProvider();
        var read = () => Registrations(provider);

        read.Should().Throw<OptionsValidationException>("a blank configured tag reaches the validator, unfiltered")
            .Which.Message.Should().Contain(nameof(VerbaraHealthCheckOptions.Tags), "the failure names the option to fix");
    }
}
