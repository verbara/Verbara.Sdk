namespace Asterisk.Sdk.Push.Tests;

public class InMemorySubscriptionRegistryTests
{
    private static SubscriberContext Sub(string tenant) =>
        new(tenant, "user", new HashSet<string>(), new HashSet<string>());

    [Fact]
    public void Register_ShouldIncrementActiveCount_WhenSubscriberRegistered()
    {
        var reg = new InMemorySubscriptionRegistry();
        reg.ActiveCount.Should().Be(0);

        using var t1 = reg.Register(Sub("tenant-1"));
        reg.ActiveCount.Should().Be(1);

        using var t2 = reg.Register(Sub("tenant-2"));
        reg.ActiveCount.Should().Be(2);
    }

    [Fact]
    public void Dispose_ShouldDecrementActiveCount_WhenSubscriberDisposed()
    {
        var reg = new InMemorySubscriptionRegistry();
        var t1 = reg.Register(Sub("tenant-1"));
        reg.ActiveCount.Should().Be(1);

        t1.Dispose();
        reg.ActiveCount.Should().Be(0);

        // idempotent dispose
        t1.Dispose();
        reg.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void CountByTenant_ShouldReturnCorrectCount_WhenMultipleTenants()
    {
        var reg = new InMemorySubscriptionRegistry();
        using var a1 = reg.Register(Sub("tenant-A"));
        using var a2 = reg.Register(Sub("tenant-A"));
        using var b1 = reg.Register(Sub("tenant-B"));

        reg.CountByTenant("tenant-A").Should().Be(2);
        reg.CountByTenant("tenant-B").Should().Be(1);
        reg.CountByTenant("tenant-Z").Should().Be(0);
    }

    [Fact]
    public async Task Register_ShouldBeThreadSafe_WhenMultipleConcurrentRegistrations()
    {
        var reg = new InMemorySubscriptionRegistry();
        var tokens = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();

        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
                tokens.Add(reg.Register(Sub("tenant-X")));
        }));
        await Task.WhenAll(tasks);

        reg.ActiveCount.Should().Be(32 * 50);
        reg.CountByTenant("tenant-X").Should().Be(32 * 50);

        foreach (var t in tokens) t.Dispose();
        reg.ActiveCount.Should().Be(0);
    }
}
