using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Hosting;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// An unusable webhook delivery setting is rejected
/// by options validation, naming the option, and both <see cref="WebhookDeliveryService"/> constructors refuse
/// it with an <see cref="ArgumentOutOfRangeException"/> whose <c>ParamName</c> is the option; the defaults and
/// the boundaries are accepted.
/// </summary>
public sealed class WebhookDeliveryOptionsRuleTests
{
    /// <summary>Each unusable setting and the option the rejection names (the first one, for the constructors).</summary>
    public static TheoryData<string, string> Unusable => new()
    {
        { "MaxDelay 500 ms below InitialDelay 1 s", nameof(WebhookDeliveryOptions.MaxDelay) },
        { "InitialDelay -50 ms", nameof(WebhookDeliveryOptions.InitialDelay) },
        { "InitialDelay infinite", nameof(WebhookDeliveryOptions.InitialDelay) },
        { "MaxDelay infinite", nameof(WebhookDeliveryOptions.MaxDelay) },
        { "MaxDelay 60 days", nameof(WebhookDeliveryOptions.MaxDelay) },
        { "MaxDelay int.MaxValue + 1 ms", nameof(WebhookDeliveryOptions.MaxDelay) },
        { "InitialDelay = MaxDelay = 60 days", nameof(WebhookDeliveryOptions.InitialDelay) },
        { "MaxRetries -1", nameof(WebhookDeliveryOptions.MaxRetries) },
        { "TimeoutPerAttempt zero", nameof(WebhookDeliveryOptions.TimeoutPerAttempt) },
        { "TimeoutPerAttempt -1 s", nameof(WebhookDeliveryOptions.TimeoutPerAttempt) },
    };

    /// <summary>The defaults and the boundary values the rule accepts.</summary>
    public static TheoryData<string> Usable => new()
    {
        "defaults",
        "InitialDelay = MaxDelay = 0",
        "MaxDelay int.MaxValue ms",
        "InitialDelay = MaxDelay = int.MaxValue ms",
        "MaxRetries 0",
        "TimeoutPerAttempt infinite",
        "TimeoutPerAttempt 1 tick",
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public void Validation_ShouldFailNamingTheOption_WhenSettingIsUnusable(string setting, string option)
    {
        using var provider = BuildProvider(setting);

        var read = () => provider.GetRequiredService<IOptions<WebhookDeliveryOptions>>().Value;

        read.Should().Throw<OptionsValidationException>($"{setting} can never be used by webhook delivery")
            .Which.Message.Should().Contain(option, "the message names the offending option");
    }

    [Fact]
    public void Validation_ShouldNameBothDelays_WhenBothAreAboveTheWaitLimit()
    {
        using var provider = BuildProvider("InitialDelay = MaxDelay = 60 days");

        var read = () => provider.GetRequiredService<IOptions<WebhookDeliveryOptions>>().Value;

        var message = read.Should().Throw<OptionsValidationException>().Which.Message;
        message.Should().Contain(nameof(WebhookDeliveryOptions.InitialDelay));
        message.Should().Contain(nameof(WebhookDeliveryOptions.MaxDelay));
    }

    [Theory]
    [MemberData(nameof(Usable))]
    public void Validation_ShouldAccept_WhenSettingIsUsable(string setting)
    {
        using var provider = BuildProvider(setting);

        var read = () => provider.GetRequiredService<IOptions<WebhookDeliveryOptions>>().Value;

        read.Should().NotThrow($"{setting} is a usable webhook delivery setting");
    }

    [Fact]
    public void HostStart_ShouldFailNamingMaxDelay_WhenMaxDelayIsBelowInitialDelay()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPush();
        services.AddVerbaraPushWebhooks(o =>
        {
            o.InitialDelay = TimeSpan.FromSeconds(1);
            o.MaxDelay = TimeSpan.FromMilliseconds(500);
        });
        using var provider = services.BuildServiceProvider();

        // The host constructs its hosted services when it starts.
        var start = () => provider.GetServices<IHostedService>().ToList();

        start.Should().Throw<OptionsValidationException>("the webhook delivery service cannot be built with these options")
            .Which.Message.Should().Contain(nameof(WebhookDeliveryOptions.MaxDelay));
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public void PublicConstructor_ShouldThrowNamingTheOption_WhenSettingIsUnusable(string setting, string option)
    {
        var options = Options.Create(Apply(setting));

        var build = () => new WebhookDeliveryService(
            Substitute.For<IPushEventBus>(), Substitute.For<IWebhookSubscriptionStore>(), Substitute.For<IWebhookSigner>(),
            Substitute.For<IHttpClientFactory>(), options, NullLoggerFactory.Instance);

        build.Should().Throw<ArgumentOutOfRangeException>($"{setting} can never be used by webhook delivery")
            .Which.ParamName.Should().Be(option);
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public void InternalConstructor_ShouldThrowNamingTheOption_WhenSettingIsUnusable(string setting, string option)
    {
        var options = Options.Create(Apply(setting));
        using var metrics = new WebhookMetrics();

        var build = () => new WebhookDeliveryService(
            Substitute.For<IPushEventBus>(), Substitute.For<IWebhookSubscriptionStore>(), Substitute.For<IWebhookSigner>(),
            new DefaultWebhookPayloadSerializer(), Substitute.For<IHttpClientFactory>(), options, metrics,
            NullLogger<WebhookDeliveryService>.Instance);

        build.Should().Throw<ArgumentOutOfRangeException>($"{setting} can never be used by webhook delivery")
            .Which.ParamName.Should().Be(option);
    }

    [Theory]
    [MemberData(nameof(Usable))]
    public void Constructors_ShouldAccept_WhenSettingIsUsable(string setting)
    {
        var options = Options.Create(Apply(setting));
        using var metrics = new WebhookMetrics();

        var buildPublic = () => new WebhookDeliveryService(
            Substitute.For<IPushEventBus>(), Substitute.For<IWebhookSubscriptionStore>(), Substitute.For<IWebhookSigner>(),
            Substitute.For<IHttpClientFactory>(), options, NullLoggerFactory.Instance).Dispose();
        var buildInternal = () => new WebhookDeliveryService(
            Substitute.For<IPushEventBus>(), Substitute.For<IWebhookSubscriptionStore>(), Substitute.For<IWebhookSigner>(),
            new DefaultWebhookPayloadSerializer(), Substitute.For<IHttpClientFactory>(), options, metrics,
            NullLogger<WebhookDeliveryService>.Instance).Dispose();

        buildPublic.Should().NotThrow($"{setting} is usable");
        buildInternal.Should().NotThrow($"{setting} is usable");
    }

    private static ServiceProvider BuildProvider(string setting)
    {
        var services = new ServiceCollection();
        services.AddVerbaraPushWebhooks(o => Configure(o, setting));
        return services.BuildServiceProvider();
    }

    private static WebhookDeliveryOptions Apply(string setting)
    {
        var options = new WebhookDeliveryOptions();
        Configure(options, setting);
        return options;
    }

    private static void Configure(WebhookDeliveryOptions o, string setting)
    {
        var waitLimit = TimeSpan.FromMilliseconds(int.MaxValue);
        switch (setting)
        {
            case "MaxDelay 500 ms below InitialDelay 1 s":
                o.InitialDelay = TimeSpan.FromSeconds(1);
                o.MaxDelay = TimeSpan.FromMilliseconds(500);
                break;
            case "InitialDelay -50 ms": o.InitialDelay = TimeSpan.FromMilliseconds(-50); break;
            case "InitialDelay infinite": o.InitialDelay = Timeout.InfiniteTimeSpan; break;
            case "MaxDelay infinite": o.MaxDelay = Timeout.InfiniteTimeSpan; break;
            case "MaxDelay 60 days": o.MaxDelay = TimeSpan.FromDays(60); break;
            case "MaxDelay int.MaxValue + 1 ms": o.MaxDelay = waitLimit + TimeSpan.FromMilliseconds(1); break;
            case "InitialDelay = MaxDelay = 60 days":
                o.InitialDelay = TimeSpan.FromDays(60);
                o.MaxDelay = TimeSpan.FromDays(60);
                break;
            case "MaxRetries -1": o.MaxRetries = -1; break;
            case "TimeoutPerAttempt zero": o.TimeoutPerAttempt = TimeSpan.Zero; break;
            case "TimeoutPerAttempt -1 s": o.TimeoutPerAttempt = TimeSpan.FromSeconds(-1); break;
            case "defaults": break;
            case "InitialDelay = MaxDelay = 0":
                o.InitialDelay = TimeSpan.Zero;
                o.MaxDelay = TimeSpan.Zero;
                break;
            case "MaxDelay int.MaxValue ms": o.MaxDelay = waitLimit; break;
            case "InitialDelay = MaxDelay = int.MaxValue ms":
                o.InitialDelay = waitLimit;
                o.MaxDelay = waitLimit;
                break;
            case "MaxRetries 0": o.MaxRetries = 0; break;
            case "TimeoutPerAttempt infinite": o.TimeoutPerAttempt = Timeout.InfiniteTimeSpan; break;
            case "TimeoutPerAttempt 1 tick": o.TimeoutPerAttempt = TimeSpan.FromTicks(1); break;
            default: throw new ArgumentOutOfRangeException(nameof(setting), setting, "unknown setting");
        }
    }
}
