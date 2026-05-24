using System.Data;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
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
    public async Task QuerySingle_ShouldThrow_WhenZeroRows()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS single_t (id int)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM single_t", static _ => { }, CancellationToken.None);
        var act = async () => await _ds.QuerySingleAsync("SELECT id, 'x' AS name FROM single_t",
            static _ => { }, Map, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task QuerySingle_ShouldReturnRow_WhenExactlyOne()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS single_one (id int)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM single_one", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("INSERT INTO single_one (id) VALUES (5)", static _ => { }, CancellationToken.None);
        var item = await _ds.QuerySingleAsync("SELECT id, 'y' AS name FROM single_one",
            static _ => { }, Map, CancellationToken.None);
        item.Should().Be(new Item(5, "y"));
    }

    [Fact]
    public async Task QueryFirstOrDefault_ShouldReturnFirst_WhenMultiple_AndDefault_WhenEmpty()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS first_t (id int)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM first_t", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("INSERT INTO first_t (id) VALUES (1), (2), (3)", static _ => { }, CancellationToken.None);
        var first = await _ds.QueryFirstOrDefaultAsync("SELECT id, 'z' AS name FROM first_t ORDER BY id",
            static _ => { }, Map, CancellationToken.None);
        first.Should().Be(new Item(1, "z"));

        await _ds.ExecuteAsync("DELETE FROM first_t", static _ => { }, CancellationToken.None);
        var none = await _ds.QueryFirstOrDefaultAsync("SELECT id, 'z' AS name FROM first_t",
            static _ => { }, Map, CancellationToken.None);
        none.Should().BeNull();
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

    [Fact]
    public async Task ExecuteOnConnection_ShouldPersistRows_WhenTransactionCommitted()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS tx_commit (id int primary key)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM tx_commit", static _ => { }, CancellationToken.None);

        await using (var conn = await _ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("INSERT INTO tx_commit (id) VALUES (@Id)",
                p => p.Add(new NpgsqlParameter("Id", 11)), tx, CancellationToken.None);
            await conn.ExecuteAsync("INSERT INTO tx_commit (id) VALUES (@Id)",
                p => p.Add(new NpgsqlParameter("Id", 13)), tx, CancellationToken.None);
            await tx.CommitAsync();
        }

        var count = await _ds.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tx_commit", static _ => { }, CancellationToken.None);
        count.Should().Be(2);
    }

    [Fact]
    public async Task QueryList_ShouldReturnAllRows_InResultSetOrder()
    {
        // QueryListAsync has implicit coverage in the round-trip test, but a dedicated
        // test makes the contract (returns ALL rows, preserves SQL ORDER BY) explicit.
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS list_t (id int primary key, name text)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM list_t", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("INSERT INTO list_t (id, name) VALUES (3, 'c'), (1, 'a'), (2, 'b')", static _ => { }, CancellationToken.None);

        var rows = await _ds.QueryListAsync("SELECT id, name FROM list_t ORDER BY id",
            static _ => { }, Map, CancellationToken.None);

        rows.Should().Equal(new Item(1, "a"), new Item(2, "b"), new Item(3, "c"));
    }

    [Fact]
    public async Task QueryList_ShouldReturnEmptyList_WhenNoRows()
    {
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS list_empty (id int)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM list_empty", static _ => { }, CancellationToken.None);

        var rows = await _ds.QueryListAsync("SELECT id, 'x' AS name FROM list_empty",
            static _ => { }, Map, CancellationToken.None);

        rows.Should().NotBeNull();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrowOperationCanceled_WhenTokenAlreadyCanceled()
    {
        // Pre-canceled token must surface to ExecuteAsync without hitting the server.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await _ds.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS never_created (id int)", static _ => { }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task QueryList_ShouldThrowOperationCanceled_WhenTokenAlreadyCanceled()
    {
        // Set up the table once with an uncanceled token, then re-query with a canceled one.
        await _ds.ExecuteAsync("CREATE TABLE IF NOT EXISTS cancel_t (id int)", static _ => { }, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await _ds.QueryListAsync(
            "SELECT id, 'x' AS name FROM cancel_t", static _ => { }, Map, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBindDbNull_WhenParameterValueIsDbNullWithExplicitType()
    {
        // Nullable Postgres columns require NpgsqlDbType to be explicit when the param
        // value is DBNull.Value — otherwise Postgres errors with 42P08 ("could not
        // determine data type of parameter"). This test pins that contract.
        await _ds.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS dbnull_t (id int primary key, note text)",
            static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM dbnull_t", static _ => { }, CancellationToken.None);

        await _ds.ExecuteAsync(
            "INSERT INTO dbnull_t (id, note) VALUES (@Id, @Note)",
            p =>
            {
                p.Add(new NpgsqlParameter("Id", 1));
                p.Add(new NpgsqlParameter("Note", NpgsqlDbType.Text) { Value = DBNull.Value });
            },
            CancellationToken.None);

        var hasNullNote = await _ds.ExecuteScalarAsync<bool>(
            "SELECT note IS NULL FROM dbnull_t WHERE id = 1",
            static _ => { }, CancellationToken.None);
        hasNullNote.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRoundTripUuid_ViaGuidParameter()
    {
        // Guid ↔ uuid is the canonical Postgres surrogate-key type. Npgsql binds Guid
        // to uuid natively; this test pins that the executor doesn't interfere with it.
        await _ds.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS uuid_t (id uuid primary key, label text)",
            static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM uuid_t", static _ => { }, CancellationToken.None);

        var id = Guid.NewGuid();
        await _ds.ExecuteAsync(
            "INSERT INTO uuid_t (id, label) VALUES (@Id, @Label)",
            p =>
            {
                p.Add(new NpgsqlParameter("Id", id));
                p.Add(new NpgsqlParameter("Label", "alpha"));
            },
            CancellationToken.None);

        var roundtripped = await _ds.QuerySingleOrDefaultAsync(
            "SELECT id, label FROM uuid_t WHERE id = @Id",
            p => p.Add(new NpgsqlParameter("Id", id)),
            static r => (r.GetGuid("id"), r.GetString("label")),
            CancellationToken.None);

        roundtripped.Should().Be((id, "alpha"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRoundTripBytea_ViaByteArrayParameter()
    {
        // bytea is the binary-blob workhorse for tokens, hashes, encrypted payloads.
        // Npgsql binds byte[] to bytea natively, but reader-side requires `GetFieldValue<byte[]>`.
        await _ds.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS bytea_t (id int primary key, payload bytea)",
            static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM bytea_t", static _ => { }, CancellationToken.None);

        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        await _ds.ExecuteAsync(
            "INSERT INTO bytea_t (id, payload) VALUES (@Id, @Payload)",
            p =>
            {
                p.Add(new NpgsqlParameter("Id", 1));
                p.Add(new NpgsqlParameter("Payload", NpgsqlDbType.Bytea) { Value = payload });
            },
            CancellationToken.None);

        var roundtripped = await _ds.QuerySingleOrDefaultAsync(
            "SELECT payload FROM bytea_t WHERE id = 1",
            static _ => { },
            static r => r.GetFieldValue<byte[]>(0),
            CancellationToken.None);

        roundtripped.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRoundTripTimestampTz_ViaDateTimeOffsetParameter()
    {
        // timestamptz is the canonical Postgres timestamp column for cross-timezone work.
        // DateTimeOffset binds with full microsecond precision per Npgsql convention.
        await _ds.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS tstz_t (id int primary key, occurred timestamptz)",
            static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM tstz_t", static _ => { }, CancellationToken.None);

        // Truncate to microseconds — Postgres' timestamptz resolution; CLR DateTimeOffset
        // has 100ns ticks, finer than what the column can persist.
        var raw = DateTimeOffset.UtcNow;
        var occurred = new DateTimeOffset(
            raw.Ticks - (raw.Ticks % (TimeSpan.TicksPerMillisecond / 1000)),
            raw.Offset);

        await _ds.ExecuteAsync(
            "INSERT INTO tstz_t (id, occurred) VALUES (@Id, @Occurred)",
            p =>
            {
                p.Add(new NpgsqlParameter("Id", 1));
                p.Add(new NpgsqlParameter("Occurred", NpgsqlDbType.TimestampTz) { Value = occurred });
            },
            CancellationToken.None);

        var roundtripped = await _ds.ExecuteScalarAsync<DateTime>(
            "SELECT occurred FROM tstz_t WHERE id = 1",
            static _ => { }, CancellationToken.None);

        // Npgsql returns timestamptz as DateTime(Utc); compare against the UTC instant.
        roundtripped.Kind.Should().Be(DateTimeKind.Utc);
        roundtripped.Should().Be(occurred.UtcDateTime);
    }
}
