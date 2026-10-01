using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ari.Client;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
}
