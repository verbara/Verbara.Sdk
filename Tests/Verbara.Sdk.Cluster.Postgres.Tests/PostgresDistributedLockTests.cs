using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Verbara.Sdk.Cluster.Postgres;
using Verbara.Sdk.Cluster.Primitives;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresDistributedLock"/> against a real Postgres 18 container.
/// Tests pick unique <c>resource</c> names per case (<see cref="Guid.NewGuid"/>) so the shared
/// fixture does not need a TRUNCATE between tests — isolation is resource-keyed and faster.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class PostgresDistributedLockTests
{
    private readonly PostgresFixture _fixture;

    public PostgresDistributedLockTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private PostgresDistributedLock CreateLock() => new(
        _fixture.DataSource,
        NullLogger<PostgresDistributedLock>.Instance,
        TimeProvider.System);

    private static string NewResource() => $"r-{Guid.NewGuid():N}";

    [Fact]
    public async Task TryAcquireAsync_ShouldReturnTrue_WhenLockIsFree()
    {
        var lk = CreateLock();
        var resource = NewResource();

        var acquired = await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30));

        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldReturnFalse_WhenLockHeldByOther()
    {
        var lk = CreateLock();
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        (await lk.TryAcquireAsync(resource, "owner-B", TimeSpan.FromSeconds(30))).Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldReturnTrue_WhenReacquiringOwnLock()
    {
        var lk = CreateLock();
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue("same owner refreshes expiry");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldRefreshExpiry_WhenReacquiringOwnLock()
    {
        var lk = CreateLock();
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(10))).Should().BeTrue();
        var initialExpiry = await ReadExpiresAt(resource);

        // Small delay so NOW() advances by at least a few ms when we re-acquire.
        await Task.Delay(50);

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        var refreshedExpiry = await ReadExpiresAt(resource);

        refreshedExpiry.Should().BeAfter(initialExpiry, "re-acquisition by same owner must extend expires_at");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldReturnTrue_WhenPreviousLockExpired()
    {
        var lk = CreateLock();
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromMilliseconds(500))).Should().BeTrue();

        // Wait past TTL so Postgres NOW() > expires_at on the next attempt.
        await Task.Delay(TimeSpan.FromSeconds(1));

        (await lk.TryAcquireAsync(resource, "owner-B", TimeSpan.FromSeconds(30))).Should().BeTrue("expired locks are reclaimable");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldElectExactlyOneWinner_WhenManyOwnersContendConcurrently()
    {
        var lk = CreateLock();
        var resource = NewResource();
        const int contenderCount = 100;

        var tasks = Enumerable.Range(0, contenderCount)
            .Select(i => Task.Run(async () =>
                await lk.TryAcquireAsync(resource, $"owner-{i:D3}", TimeSpan.FromSeconds(30))))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(won => won).Should().Be(1, "Postgres atomic upsert must elect exactly one winner");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldRemoveLock_WhenOwnerMatches()
    {
        var lk = CreateLock();
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        await lk.ReleaseAsync(resource, "owner-A");

        // Another owner must now be able to acquire freely.
        (await lk.TryAcquireAsync(resource, "owner-B", TimeSpan.FromSeconds(30))).Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldBeNoOp_WhenOwnerDoesNotMatch()
    {
        var lk = CreateLock();
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();

        await lk.ReleaseAsync(resource, "owner-B"); // wrong owner — must be no-op

        // owner-A still holds it: a fresh contender must be rejected.
        (await lk.TryAcquireAsync(resource, "owner-C", TimeSpan.FromSeconds(30))).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldBeNoOp_WhenLockDoesNotExist()
    {
        var lk = CreateLock();
        var resource = NewResource();

        var act = async () => await lk.ReleaseAsync(resource, "owner-A");

        await act.Should().NotThrowAsync("Release on a non-existent lock is a no-op");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldHandleVeryShortExpiry_WithoutNegativeInterval()
    {
        var lk = CreateLock();
        var resource = NewResource();

        var acquired = await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(1));

        acquired.Should().BeTrue("1-second TTL is the lower bound used by the leader-election service");
    }

    /// <summary>Read the current <c>expires_at</c> for a given resource (test-only helper).</summary>
    private async Task<DateTime> ReadExpiresAt(string resource)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT expires_at FROM cluster_distributed_lock WHERE resource = @r", conn);
        cmd.Parameters.AddWithValue("r", resource);
        var raw = await cmd.ExecuteScalarAsync();
        return raw is DateTime dt
            ? dt
            : throw new InvalidOperationException($"No lock row found for resource {resource}.");
    }
}
