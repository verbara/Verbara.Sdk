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
}
