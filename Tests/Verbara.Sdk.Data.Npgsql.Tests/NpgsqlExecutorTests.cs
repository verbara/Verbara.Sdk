using FluentAssertions;
using Npgsql;
using Xunit;

namespace Verbara.Sdk.Data.Npgsql.Tests;

[Collection("postgres")]
public sealed class NpgsqlExecutorTests
{
    private readonly NpgsqlDataSource _ds;
    public NpgsqlExecutorTests(PostgresFixture fx) => _ds = fx.DataSource;

    private sealed record Item(int Id, string Name);
    private static Item Map(NpgsqlDataReader r) => new(r.GetInt32("id"), r.GetString("name"));

    [Fact]
    public async Task Execute_Query_Scalar_ShouldRoundTrip()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS items (id int primary key, name text)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM items", static _ => { }, CancellationToken.None);

        var rows = await _ds.ExecuteAsync("INSERT INTO items (id, name) VALUES (@Id, @Name)",
            p => { p.Add(new NpgsqlParameter("Id", 1)); p.Add(new NpgsqlParameter("Name", "alpha")); }, CancellationToken.None);
        rows.Should().Be(1);

        var single = await _ds.QuerySingleOrDefaultAsync("SELECT id, name FROM items WHERE id = @Id",
            p => p.Add(new NpgsqlParameter("Id", 1)), Map, CancellationToken.None);
        single.Should().Be(new Item(1, "alpha"));

        var missing = await _ds.QuerySingleOrDefaultAsync("SELECT id, name FROM items WHERE id = @Id",
            p => p.Add(new NpgsqlParameter("Id", 999)), Map, CancellationToken.None);
        missing.Should().BeNull();

        var list = await _ds.QueryListAsync("SELECT id, name FROM items ORDER BY id", static _ => { }, Map, CancellationToken.None);
        list.Should().ContainSingle().Which.Should().Be(new Item(1, "alpha"));

        var count = await _ds.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM items", static _ => { }, CancellationToken.None);
        count.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteScalar_ShouldReturnDefault_WhenResultIsNull()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS empty_t (id int)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM empty_t", static _ => { }, CancellationToken.None);
        var max = await _ds.ExecuteScalarAsync<int?>("SELECT MAX(id) FROM empty_t", static _ => { }, CancellationToken.None);
        max.Should().BeNull();
    }

    [Fact]
    public async Task QuerySingleOrDefault_ShouldThrow_WhenMoreThanOneRow()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS multi (id int)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM multi", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("INSERT INTO multi (id) VALUES (1), (2)", static _ => { }, CancellationToken.None);

        var act = async () => await _ds.QuerySingleOrDefaultAsync(
            "SELECT id, 'x' AS name FROM multi ORDER BY id",
            static _ => { }, Map, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteOnConnection_ShouldHonorTransaction_WhenRolledBack()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS tx_items (id int primary key)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM tx_items", static _ => { }, CancellationToken.None);
        await using (var conn = await _ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("INSERT INTO tx_items (id) VALUES (@Id)",
                p => p.Add(new NpgsqlParameter("Id", 7)), tx, CancellationToken.None);
            await tx.RollbackAsync();
        }
        var count = await _ds.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tx_items", static _ => { }, CancellationToken.None);
        count.Should().Be(0);
    }
}
