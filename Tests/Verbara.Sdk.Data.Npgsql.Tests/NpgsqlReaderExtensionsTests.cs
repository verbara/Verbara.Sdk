using FluentAssertions;
using Npgsql;
using Xunit;

namespace Verbara.Sdk.Data.Npgsql.Tests;

[Collection("postgres")]
public sealed class NpgsqlReaderExtensionsTests
{
    private readonly NpgsqlDataSource _dataSource;
    public NpgsqlReaderExtensionsTests(PostgresFixture fx) => _dataSource = fx.DataSource;

    [Fact]
    public async Task GetGetters_ShouldReadTypedValues_WhenColumnsPresent()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using (var ddl = new NpgsqlCommand(
            "CREATE TEMP TABLE t (s text, n int, b boolean, ts timestamptz, g uuid, d date, nn int)", conn))
            await ddl.ExecuteNonQueryAsync();
        var g = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO t VALUES ('hi', 42, true, '2026-05-19T10:00:00Z', @g, '2026-05-19', NULL)", conn))
        {
            ins.Parameters.Add(new NpgsqlParameter("g", g));
            await ins.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand("SELECT s, n, b, ts, g, d, nn FROM t", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();

        r.GetString("s").Should().Be("hi");
        r.GetInt32("n").Should().Be(42);
        r.GetBoolean("b").Should().BeTrue();
        r.GetDateTimeOffset("ts").Should().Be(new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero));
        r.GetGuid("g").Should().Be(g);
        r.GetDateOnly("d").Should().Be(new DateOnly(2026, 5, 19));
        r.GetInt32OrNull("nn").Should().BeNull();
        r.GetStringOrNull("s").Should().Be("hi");
    }

    [Fact]
    public async Task GetGetters_ShouldReadAllExtendedTypes_AndNullVariants()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        // Table: one value row + one all-null row to exercise every getter and its OrNull variant
        await using (var ddl = new NpgsqlCommand(
            "CREATE TEMP TABLE ext (" +
            "  i8 bigint, i2 smallint, dec numeric(10,2), dbl float8," +
            "  ts timestamp, tstz timestamptz, i8n bigint, bn boolean, sn text," +
            "  dton timestamptz" +
            ")", conn))
            await ddl.ExecuteNonQueryAsync();

        await using (var ins = new NpgsqlCommand(
            "INSERT INTO ext VALUES " +
            "  (9876543210, 32767, 12345.67, 3.14, '2026-01-02T03:04:05', '2026-01-02T03:04:05Z', 42, true, 'hello', '2026-01-02T03:04:05Z')," +
            "  (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)", conn))
            await ins.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT i8, i2, dec, dbl, ts, tstz, i8n, bn, sn, dton FROM ext ORDER BY i8 NULLS LAST", conn);
        await using var r = await cmd.ExecuteReaderAsync();

        // --- Value row ---
        (await r.ReadAsync()).Should().BeTrue();

        r.GetInt64("i8").Should().Be(9876543210L);
        r.GetInt16("i2").Should().Be((short)32767);
        r.GetDecimal("dec").Should().Be(12345.67m);
        r.GetDouble("dbl").Should().BeApproximately(3.14, 0.001);
        r.GetDateTime("ts").Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));
        r.GetDateTimeOffset("tstz").Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        r.GetInt64OrNull("i8n").Should().Be(42L);
        r.GetBooleanOrNull("bn").Should().BeTrue();
        r.GetStringOrNull("sn").Should().Be("hello");
        r.GetDateTimeOrNull("ts").Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));
        r.GetDateTimeOffsetOrNull("dton").Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        // --- All-null row ---
        (await r.ReadAsync()).Should().BeTrue();

        r.GetInt64OrNull("i8n").Should().BeNull();
        r.GetBooleanOrNull("bn").Should().BeNull();
        r.GetStringOrNull("sn").Should().BeNull();
        r.GetDateTimeOrNull("ts").Should().BeNull();
        r.GetDateTimeOffsetOrNull("dton").Should().BeNull();
    }
}
