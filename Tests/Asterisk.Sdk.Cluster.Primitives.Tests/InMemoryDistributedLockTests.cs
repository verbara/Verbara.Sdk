using Asterisk.Sdk.Cluster.Primitives.InMemory;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Cluster.Primitives.Tests;

public sealed class InMemoryDistributedLockTests
{
    [Fact]
    public async Task TryAcquire_ShouldSucceed_WhenLockUnheld()
    {
        var locks = new InMemoryDistributedLock();

        var acquired = await locks.TryAcquireAsync("resource-1", "owner-A", TimeSpan.FromSeconds(30));

        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquire_ShouldFail_WhenHeldByAnotherOwner()
    {
        var locks = new InMemoryDistributedLock();

        (await locks.TryAcquireAsync("r", "A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        (await locks.TryAcquireAsync("r", "B", TimeSpan.FromSeconds(30))).Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_WhenSameOwnerReacquires()
    {
        var locks = new InMemoryDistributedLock();

        (await locks.TryAcquireAsync("r", "A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        (await locks.TryAcquireAsync("r", "A", TimeSpan.FromSeconds(30))).Should().BeTrue("same owner must be able to refresh");
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_AfterExpiryElapses()
    {
        var time = new FakeTimeProvider();
        var locks = new InMemoryDistributedLock(time);

        (await locks.TryAcquireAsync("r", "A", TimeSpan.FromSeconds(1))).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(2));
        (await locks.TryAcquireAsync("r", "B", TimeSpan.FromSeconds(1))).Should().BeTrue("B must acquire after A's lock expires");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldBeNoOp_WhenOwnerDoesNotMatch()
    {
        var locks = new InMemoryDistributedLock();

        await locks.TryAcquireAsync("r", "A", TimeSpan.FromSeconds(30));
        await locks.ReleaseAsync("r", "B", default); // different owner — no-op

        (await locks.TryAcquireAsync("r", "C", TimeSpan.FromSeconds(30))).Should().BeFalse("A still owns the lock");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldAllowReacquisitionByAnotherOwner()
    {
        var locks = new InMemoryDistributedLock();

        await locks.TryAcquireAsync("r", "A", TimeSpan.FromSeconds(30));
        await locks.ReleaseAsync("r", "A", default);

        (await locks.TryAcquireAsync("r", "B", TimeSpan.FromSeconds(30))).Should().BeTrue();
    }
}
