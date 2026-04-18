using Asterisk.Sdk.Push.Topics;
using Asterisk.Sdk.Push.Webhooks;
using FluentAssertions;

namespace Asterisk.Sdk.Push.Webhooks.Tests;

public sealed class InMemoryWebhookSubscriptionStoreTests
{
    [Fact]
    public async Task GetAllAsync_ShouldReturnEmpty_WhenNoSubscriptionsAdded()
    {
        var store = new InMemoryWebhookSubscriptionStore();

        var all = await store.GetAllAsync();

        all.Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_ShouldAddSubscription_WhenCalled()
    {
        var store = new InMemoryWebhookSubscriptionStore();
        var sub = BuildSubscription("sub-1");

        await store.AddAsync(sub);

        var all = await store.GetAllAsync();
        all.Should().ContainSingle(s => s.Id == "sub-1");
    }

    [Fact]
    public async Task AddAsync_ShouldOverwrite_WhenSameIdUsedTwice()
    {
        var store = new InMemoryWebhookSubscriptionStore();

        await store.AddAsync(BuildSubscription("sub-1", url: "https://a.example.com"));
        await store.AddAsync(BuildSubscription("sub-1", url: "https://b.example.com"));

        var all = await store.GetAllAsync();
        all.Should().ContainSingle(s => s.TargetUrl.Host == "b.example.com");
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemove_WhenIdExists()
    {
        var store = new InMemoryWebhookSubscriptionStore();
        await store.AddAsync(BuildSubscription("sub-1"));

        await store.RemoveAsync("sub-1");

        var all = await store.GetAllAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_ShouldNotThrow_WhenIdDoesNotExist()
    {
        var store = new InMemoryWebhookSubscriptionStore();

        var act = async () => await store.RemoveAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    private static WebhookSubscription BuildSubscription(string id, string? url = null) => new()
    {
        Id = id,
        TopicPattern = TopicPattern.Parse("calls.**"),
        TargetUrl = new Uri(url ?? "https://example.com/hook"),
    };
}
