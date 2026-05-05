using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Push.Webhooks.Tests;

public sealed class WebhookServiceCollectionExtensionsTests
{
    [Fact]
    public void AddVerbaraPushWebhooks_ShouldRegisterInMemorySubscriptionStore_ByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPush();

        services.AddVerbaraPushWebhooks();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IWebhookSubscriptionStore>().Should().BeOfType<InMemoryWebhookSubscriptionStore>();
    }

    [Fact]
    public void AddVerbaraPushWebhooks_ShouldRegisterHmacSigner_ByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPush();

        services.AddVerbaraPushWebhooks();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IWebhookSigner>().Should().BeOfType<HmacSha256Signer>();
    }

    [Fact]
    public void AddVerbaraPushWebhooks_ShouldApplyOptions_WhenConfigureCallbackProvided()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPush();

        services.AddVerbaraPushWebhooks(opts =>
        {
            opts.MaxRetries = 10;
            opts.InitialDelay = TimeSpan.FromSeconds(2);
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<WebhookDeliveryOptions>>().Value;
        opts.MaxRetries.Should().Be(10);
        opts.InitialDelay.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddVerbaraPushWebhooks_ShouldNotOverride_WhenConsumerRegistersSubscriptionStoreFirst()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPush();
        services.AddSingleton<IWebhookSubscriptionStore, CustomStore>();

        services.AddVerbaraPushWebhooks();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IWebhookSubscriptionStore>().Should().BeOfType<CustomStore>();
    }

    private sealed class CustomStore : IWebhookSubscriptionStore
    {
        public ValueTask<IReadOnlyList<WebhookSubscription>> GetAllAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<WebhookSubscription>>([]);
        public ValueTask AddAsync(WebhookSubscription subscription, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
