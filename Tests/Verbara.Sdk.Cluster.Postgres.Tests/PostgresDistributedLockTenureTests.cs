using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

/// <summary>
/// <c>acquired_at</c> on the cluster lock row: the time the current owner <em>name</em> took the lock.
/// A renewal by the same owner before expiry keeps it; any acquisition of a free or expired row stamps
/// the server's <c>NOW()</c>. Every bound is read from the server clock (<c>clock_timestamp()</c>), never
/// from the test host's.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class PostgresDistributedLockTenureTests
{
    private readonly PostgresFixture _fixture;

    public PostgresDistributedLockTenureTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static PostgresDistributedLock CreateLock(NpgsqlDataSource dataSource) => new(
        dataSource,
        NullLogger<PostgresDistributedLock>.Instance,
        TimeProvider.System);

    private static string NewResource() => $"r-{Guid.NewGuid():N}";

    [Fact]
    public async Task TryAcquireAsync_ShouldStampTheServerTimeAsAcquiredAt_WhenTheLockIsFree()
    {
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();

        var before = await LockRows.ServerClockAsync(_fixture.DataSource);
        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        var after = await LockRows.ServerClockAsync(_fixture.DataSource);

        var row = await LockRows.ReadAsync(_fixture.DataSource, resource);
        row.AcquiredAt.Should().NotBeNull("a lock taken by this SDK knows since when its owner holds it");
        row.AcquiredAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldKeepAcquiredAt_WhenTheSameOwnerRenewsBeforeExpiry()
    {
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        var first = await LockRows.ReadAsync(_fixture.DataSource, resource);
        await LockRows.WaitForServerClockPastAsync(_fixture.DataSource, first.AcquiredAt!.Value);

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        var renewed = await LockRows.ReadAsync(_fixture.DataSource, resource);

        renewed.ExpiresAt.Should().BeAfter(first.ExpiresAt, "the renewal still extends the expiry");
        renewed.AcquiredAt.Should().Be(first.AcquiredAt, "a renewal before expiry is the same tenure");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldStampANewAcquiredAt_WhenTheSameOwnerReacquiresAfterExpiry()
    {
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromMilliseconds(300))).Should().BeTrue();
        var first = await LockRows.ReadAsync(_fixture.DataSource, resource);
        await LockRows.WaitPastExpiryAsync(_fixture.DataSource, resource);

        var before = await LockRows.ServerClockAsync(_fixture.DataSource);
        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        var again = await LockRows.ReadAsync(_fixture.DataSource, resource);

        again.AcquiredAt.Should().NotBeNull();
        again.AcquiredAt!.Value.Should().BeAfter(first.AcquiredAt!.Value, "an expired lock ended its tenure");
        again.AcquiredAt.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldStampANewAcquiredAt_WhenAnotherOwnerTakesAnExpiredLock()
    {
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromMilliseconds(300))).Should().BeTrue();
        await LockRows.WaitPastExpiryAsync(_fixture.DataSource, resource);

        var before = await LockRows.ServerClockAsync(_fixture.DataSource);
        (await lk.TryAcquireAsync(resource, "owner-B", TimeSpan.FromSeconds(30))).Should().BeTrue();
        var taken = await LockRows.ReadAsync(_fixture.DataSource, resource);

        taken.Owner.Should().Be("owner-B");
        taken.AcquiredAt.Should().NotBeNull();
        taken.AcquiredAt!.Value.Should().BeOnOrAfter(before, "a new owner starts its own tenure");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldLeaveTheRowUnchanged_WhenAnotherOwnerIsRejected()
    {
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();
        var held = await LockRows.ReadAsync(_fixture.DataSource, resource);

        (await lk.TryAcquireAsync(resource, "owner-B", TimeSpan.FromSeconds(30))).Should().BeFalse();
        var after = await LockRows.ReadAsync(_fixture.DataSource, resource);

        after.Should().Be(held, "a rejected contender writes nothing");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldKeepAcquiredAtUnknown_WhenTheSameOwnerRenewsARowWithNoAcquiredAt()
    {
        // A row written before V002, or by a node still on the previous SDK: acquired_at is NULL. A renewal
        // keeps it NULL ("unknown") — the SDK never invents a time for a tenure it did not see start.
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();
        await LockRows.PreviousSdkAcquireAsync(_fixture.DataSource, resource, "owner-A", TimeSpan.FromSeconds(30));

        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();

        var row = await LockRows.ReadAsync(_fixture.DataSource, resource);
        row.Owner.Should().Be("owner-A");
        row.AcquiredAt.Should().BeNull("the tenure began before anything recorded it");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldStampAcquiredAt_WhenARowWithNoAcquiredAtHasExpired()
    {
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();
        await LockRows.PreviousSdkAcquireAsync(_fixture.DataSource, resource, "owner-A", TimeSpan.FromMilliseconds(300));
        await LockRows.WaitPastExpiryAsync(_fixture.DataSource, resource);

        var before = await LockRows.ServerClockAsync(_fixture.DataSource);
        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromSeconds(30))).Should().BeTrue();

        var row = await LockRows.ReadAsync(_fixture.DataSource, resource);
        row.AcquiredAt.Should().NotBeNull("an acquisition after expiry starts a tenure this SDK saw");
        row.AcquiredAt!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task PreviousSdkAcquire_ShouldInheritTheOldAcquiredAt_WhenItTakesAnExpiredLock()
    {
        // The staggered-deploy case the reader's max rule exists for: a node still on the previous SDK
        // only sets owner and expires_at, so its takeover after expiry leaves the previous tenure's time.
        var lk = CreateLock(_fixture.DataSource);
        var resource = NewResource();
        (await lk.TryAcquireAsync(resource, "owner-A", TimeSpan.FromMilliseconds(300))).Should().BeTrue();
        var first = await LockRows.ReadAsync(_fixture.DataSource, resource);
        await LockRows.WaitPastExpiryAsync(_fixture.DataSource, resource);

        (await LockRows.PreviousSdkAcquireAsync(_fixture.DataSource, resource, "owner-B", TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        var taken = await LockRows.ReadAsync(_fixture.DataSource, resource);
        taken.Owner.Should().Be("owner-B");
        taken.AcquiredAt.Should().Be(first.AcquiredAt, "the previous SDK's upsert does not know the column");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldKeepTheCrashedProcesssAcquiredAt_WhenItsRestartReacquiresBeforeExpiry()
    {
        // The owner is the machine or pod name, stable across a restart. A process that dies without
        // releasing leaves its row; the restarted process, same owner name, renews it before expiry and
        // inherits the dead process's acquired_at. That is the documented semantics (tenure of the owner
        // name, not of the process): a reader that wants its own tenure takes max(acquired_at, its own
        // first observation).
        var resource = NewResource();
        LockRow beforeCrash;
        await using (var crashed = NpgsqlDataSource.Create(_fixture.ConnectionString))
        {
            (await CreateLock(crashed).TryAcquireAsync(resource, "node-1", TimeSpan.FromSeconds(30))).Should().BeTrue();
            beforeCrash = await LockRows.ReadAsync(crashed, resource);
        } // dies here: no ReleaseAsync, its connections are gone

        await using var restarted = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var lk = CreateLock(restarted);
        (await lk.TryAcquireAsync(resource, "node-2", TimeSpan.FromSeconds(30)))
            .Should().BeFalse("the dead process's row still holds the lock until it expires");
        (await lk.TryAcquireAsync(resource, "node-1", TimeSpan.FromSeconds(30))).Should().BeTrue();

        var afterRestart = await LockRows.ReadAsync(restarted, resource);
        afterRestart.Owner.Should().Be("node-1");
        afterRestart.AcquiredAt.Should().NotBeNull();
        afterRestart.AcquiredAt.Should().Be(beforeCrash.AcquiredAt, "acquired_at measures the owner name's tenure");
        afterRestart.ExpiresAt.Should().BeAfter(beforeCrash.ExpiresAt);
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldStampANewAcquiredAt_WhenTheCrashedProcesssLockExpiresBeforeItsRestart()
    {
        var resource = NewResource();
        LockRow beforeCrash;
        await using (var crashed = NpgsqlDataSource.Create(_fixture.ConnectionString))
        {
            (await CreateLock(crashed).TryAcquireAsync(resource, "node-1", TimeSpan.FromMilliseconds(300))).Should().BeTrue();
            beforeCrash = await LockRows.ReadAsync(crashed, resource);
        }

        await LockRows.WaitPastExpiryAsync(_fixture.DataSource, resource);
        await using var restarted = NpgsqlDataSource.Create(_fixture.ConnectionString);
        (await CreateLock(restarted).TryAcquireAsync(resource, "node-1", TimeSpan.FromSeconds(30))).Should().BeTrue();

        var afterRestart = await LockRows.ReadAsync(restarted, resource);
        afterRestart.AcquiredAt.Should().NotBeNull();
        afterRestart.AcquiredAt!.Value.Should().BeAfter(beforeCrash.AcquiredAt!.Value, "the dead tenure ended at expiry");
    }
}

/// <summary>One <c>cluster_distributed_lock</c> row as the tests read it.</summary>
internal sealed record LockRow(string Owner, DateTime ExpiresAt, DateTime? AcquiredAt);

/// <summary>Test-only SQL against the lock table, kept apart from the code under test.</summary>
internal static class LockRows
{
    /// <summary>The acquire statement the previous SDK (before V002) sends, verbatim.</summary>
    private const string PreviousSdkAcquireSql = """
        INSERT INTO cluster_distributed_lock (resource, owner, expires_at)
        VALUES (@resource, @owner, NOW() + @expiry_interval)
        ON CONFLICT (resource) DO UPDATE
        SET owner = EXCLUDED.owner, expires_at = EXCLUDED.expires_at
        WHERE cluster_distributed_lock.owner = @owner
           OR cluster_distributed_lock.expires_at < NOW()
        RETURNING owner = @owner AS won;
        """;

    public static async Task<LockRow> ReadAsync(NpgsqlDataSource dataSource, string resource)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT owner, expires_at, acquired_at FROM cluster_distributed_lock WHERE resource = @r");
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = resource });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"No lock row found for resource {resource}.");
        return new LockRow(
            reader.GetString(0),
            reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    public static async Task<DateTime> ServerClockAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand("SELECT clock_timestamp()");
        return (DateTime)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Waits until the server clock is past <paramref name="instant"/>, so the next statement's NOW() differs.</summary>
    public static async Task WaitForServerClockPastAsync(NpgsqlDataSource dataSource, DateTime instant)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await ServerClockAsync(dataSource) <= instant)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The server clock did not advance within 10 s.");
            await Task.Delay(1); // fence-allow: LOOP-DRIVER — polls the server clock; the exit is the clock condition
        }
    }

    /// <summary>Waits until the server clock is past the row's <c>expires_at</c>.</summary>
    public static async Task WaitPastExpiryAsync(NpgsqlDataSource dataSource, string resource)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await using var cmd = dataSource.CreateCommand(
                "SELECT expires_at < NOW() FROM cluster_distributed_lock WHERE resource = @r");
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = resource });
            if ((bool)(await cmd.ExecuteScalarAsync())!)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Lock row {resource} did not expire within 10 s of server time.");
            await Task.Delay(100); // fence-allow: LOOP-DRIVER — polls the row against the server clock; the exit is the expiry condition
        }
    }

    public static async Task<bool> PreviousSdkAcquireAsync(
        NpgsqlDataSource dataSource, string resource, string owner, TimeSpan expiry)
    {
        await using var cmd = dataSource.CreateCommand(PreviousSdkAcquireSql);
        cmd.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Text) { Value = resource });
        cmd.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = owner });
        cmd.Parameters.Add(new NpgsqlParameter("expiry_interval", NpgsqlDbType.Interval) { Value = expiry });
        return await cmd.ExecuteScalarAsync() is true;
    }
}
